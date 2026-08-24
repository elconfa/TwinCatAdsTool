using System;
using System.Collections.Generic;
using System.Linq;
using TwinCAT.TypeSystem;
using TwinCatAdsTool.Interfaces.Values;

namespace TwinCatAdsTool.Logic.Values
{
    /// <summary>
    /// Wraps the <see cref="DynamicValue"/> tree that the ads library builds from a single read
    /// of a whole symbol. Every member access below stays in memory - no ads round trip is
    /// triggered while walking the tree, which is the whole point of reading the symbol in one go.
    /// </summary>
    public class DynamicValueNode : IMutablePlcValueNode
    {
        private readonly object value;
        private IReadOnlyList<string> memberNames;

        public DynamicValueNode(object value)
        {
            this.value = value;
        }

        private DynamicValue Dynamic => value as DynamicValue;

        /// <summary>
        /// An array of a primitive plc type comes back as a plain managed array - bool[] for an
        /// ARRAY OF BOOL, byte[] for an ARRAY OF BYTE - rather than as a DynamicValue. Treating
        /// one of those as a leaf would hand a whole array to JValue, which cannot type it.
        /// </summary>
        private Array NativeArray => value as Array;

        public bool IsArray => Dynamic?.DataType?.Category == DataTypeCategory.Array || NativeArray != null;

        public bool IsStruct
        {
            get
            {
                var dynamicValue = Dynamic;
                if (dynamicValue == null || dynamicValue.IsPrimitive || IsArray)
                {
                    return false;
                }

                return MemberNamesCore.Count > 0;
            }
        }

        public IEnumerable<IPlcValueNode> Elements
        {
            get
            {
                var native = NativeArray;
                if (native != null)
                {
                    return native.Cast<object>().Select(element => (IPlcValueNode) new DynamicValueNode(element));
                }

                if (!IsArray || !Dynamic.TryGetArrayElementValues(out var elements))
                {
                    return Enumerable.Empty<IPlcValueNode>();
                }

                return elements.Select(element => (IPlcValueNode) new DynamicValueNode(element));
            }
        }

        public IEnumerable<string> MemberNames => MemberNamesCore;

        private IReadOnlyList<string> MemberNamesCore
            => memberNames ?? (memberNames = Dynamic?.GetDynamicMemberNames()?.ToList() ?? (IReadOnlyList<string>) Array.Empty<string>());

        public object Value => ValueCoercion.Normalize(value);

        public int ArrayLowerBound
        {
            get
            {
                var native = NativeArray;
                if (native != null)
                {
                    // A managed array is indexed from its own lower bound, not from the one the
                    // plc declares; both ends of the conversion agree on it, which is what counts.
                    return native.Rank == 1 ? native.GetLowerBound(0) : 0;
                }

                var dimensions = (Dynamic?.DataType as IArrayType)?.Dimensions;
                return dimensions?.LowerBounds?.FirstOrDefault() ?? 0;
            }
        }

        public int ArrayLength
        {
            get
            {
                var native = NativeArray;
                if (native != null)
                {
                    return native.Length;
                }

                return (Dynamic?.DataType as IArrayType)?.Dimensions?.ElementCount ?? 0;
            }
        }

        public bool TryGetMember(string name, out IPlcValueNode member)
        {
            var found = TryGetMutableMember(name, out var mutable);
            member = mutable;
            return found;
        }

        public bool TryGetMutableMember(string name, out IMutablePlcValueNode member)
        {
            member = null;

            var dynamicValue = Dynamic;
            if (dynamicValue == null || !dynamicValue.TryGetMemberValue(name, out var memberValue))
            {
                return false;
            }

            member = new DynamicValueNode(memberValue);
            return true;
        }

        public bool TryGetMutableElement(int index, out IMutablePlcValueNode element)
        {
            element = null;

            var native = NativeArray;
            if (native != null)
            {
                try
                {
                    element = new DynamicValueNode(native.GetValue(index));
                    return true;
                }
                catch (Exception)
                {
                    // Out of range, or an array of more than one dimension.
                    return false;
                }
            }

            var dynamicValue = Dynamic;
            if (dynamicValue == null || !dynamicValue.TryGetIndexValue(new[] {index}, out var elementValue))
            {
                return false;
            }

            element = new DynamicValueNode(elementValue);
            return true;
        }

        public bool TrySetMember(string name, object newValue)
        {
            var dynamicValue = Dynamic;
            if (dynamicValue == null || !dynamicValue.TryGetMemberValue(name, out var current))
            {
                return false;
            }

            return ValueCoercion.TryCoerce(newValue, ValueCoercion.Normalize(current), out var coerced) &&
                   dynamicValue.TrySetMemberValue(name, Denormalize(coerced, current));
        }

        public bool TrySetElement(int index, object newValue)
        {
            var native = NativeArray;
            if (native != null)
            {
                try
                {
                    var element = native.GetValue(index);
                    if (!ValueCoercion.TryCoerce(newValue, ValueCoercion.Normalize(element), out var converted))
                    {
                        return false;
                    }

                    native.SetValue(Denormalize(converted, element), index);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            var dynamicValue = Dynamic;
            if (dynamicValue == null || !dynamicValue.TryGetIndexValue(new[] {index}, out var current))
            {
                return false;
            }

            return ValueCoercion.TryCoerce(newValue, ValueCoercion.Normalize(current), out var coerced) &&
                   dynamicValue.TrySetIndexValue(new object[] {index}, Denormalize(coerced, current));
        }

        /// <summary>
        /// <see cref="ValueCoercion.TryCoerce"/> works against the normalized value, so a PlcOpen
        /// member comes back as DateTime or TimeSpan and has to be wrapped again before writing.
        /// </summary>
        private static object Denormalize(object coerced, object current)
            => ValueCoercion.TryCoerce(coerced, current, out var wrapped) ? wrapped : coerced;
    }
}
