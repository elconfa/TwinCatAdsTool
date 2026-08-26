using System;
using System.Collections.Generic;
using TwinCatAdsTool.Interfaces.Values;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// In-memory stand-in for a plc value tree, so the json conversion and the restore plan can
    /// be tested without a plc or the ads library.
    /// </summary>
    public class FakeValueNode : IPlcValueNode
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

        // Nothing here wraps a value the way the PlcOpen types do, so both views are the same.
        public object NativeValue => Value;

        public int ArrayLowerBound { get; private set; }

        public bool TryGetMember(string name, out IPlcValueNode member)
        {
            var found = members.TryGetValue(name, out var node);
            member = node;
            return found;
        }

        public object MemberValue(string name) => members[name].Value;
    }
}
