using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatAdsTool.Interfaces.Values;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// In-memory stand-in for a plc value tree, so the json conversion can be tested without
    /// a plc or the ads library.
    /// </summary>
    public class FakeValueNode : IMutablePlcValueNode
    {
        private readonly List<string> memberOrder = new List<string>();
        private readonly Dictionary<string, FakeValueNode> members = new Dictionary<string, FakeValueNode>(StringComparer.OrdinalIgnoreCase);
        private readonly List<FakeValueNode> elements = new List<FakeValueNode>();

        private FakeValueNode()
        {
        }

        public static FakeValueNode Leaf(object value) => new FakeValueNode {Value = value, kind = Kind.Leaf};

        public static FakeValueNode Struct(params (string Name, FakeValueNode Node)[] members)
        {
            var node = new FakeValueNode {kind = Kind.Struct};
            foreach (var (name, member) in members)
            {
                node.memberOrder.Add(name);
                node.members[name] = member;
            }

            return node;
        }

        public static FakeValueNode Array(int lowerBound, params FakeValueNode[] elements)
        {
            var node = new FakeValueNode {kind = Kind.Array, ArrayLowerBound = lowerBound};
            node.elements.AddRange(elements);
            return node;
        }

        private enum Kind
        {
            Leaf,
            Struct,
            Array
        }

        private Kind kind;

        public bool IsArray => kind == Kind.Array;
        public bool IsStruct => kind == Kind.Struct;
        public IEnumerable<IPlcValueNode> Elements => elements;
        public IEnumerable<string> MemberNames => memberOrder;
        public object Value { get; private set; }
        public int ArrayLowerBound { get; private set; }
        public int ArrayLength => elements.Count;

        public bool TryGetMember(string name, out IPlcValueNode member)
        {
            var found = members.TryGetValue(name, out var node);
            member = node;
            return found;
        }

        public bool TryGetMutableMember(string name, out IMutablePlcValueNode member)
        {
            var found = members.TryGetValue(name, out var node);
            member = node;
            return found;
        }

        public bool TryGetMutableElement(int index, out IMutablePlcValueNode element)
        {
            element = null;
            var offset = index - ArrayLowerBound;
            if (offset < 0 || offset >= elements.Count)
            {
                return false;
            }

            element = elements[offset];
            return true;
        }

        public bool TrySetMember(string name, object value)
            => members.TryGetValue(name, out var node) && Assign(node, value);

        public bool TrySetElement(int index, object value)
        {
            var offset = index - ArrayLowerBound;
            return offset >= 0 && offset < elements.Count && Assign(elements[offset], value);
        }

        /// <summary>
        /// Mimics a typed plc variable: the value is converted to the type the variable already
        /// holds, exactly as <c>DynamicValueNode</c> does through ValueCoercion, and refused when
        /// no conversion exists.
        /// </summary>
        private static bool Assign(FakeValueNode node, object value)
        {
            if (node.kind != Kind.Leaf)
            {
                return false;
            }

            if (node.Value != null && value != null && node.Value.GetType() != value.GetType())
            {
                try
                {
                    value = Convert.ChangeType(value, node.Value.GetType());
                }
                catch (Exception)
                {
                    return false;
                }
            }

            node.Value = value;
            return true;
        }

        public object MemberValue(string name) => members[name].Value;
        public object ElementValue(int index) => elements[index - ArrayLowerBound].Value;
    }
}
