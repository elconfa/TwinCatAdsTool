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

        public bool IsArray => Dynamic?.DataType?.Category == DataTypeCategory.Array;

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
                var dimensions = (Dynamic?.DataType as IArrayType)?.Dimensions;
                return dimensions?.LowerBounds?.FirstOrDefault() ?? 0;
            }
        }

        public int ArrayLength => (Dynamic?.DataType as IArrayType)?.Dimensions?.ElementCount ?? 0;

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
