using System.Collections.Generic;

namespace TwinCatAdsTool.Interfaces.Values
{
    /// <summary>
    /// A node of an already-read plc value tree: a struct, an array or a leaf.
    /// Navigating this tree must never cause ads traffic - the whole value is expected to have
    /// been transferred in a single read beforehand. Keeping this behind an interface lets the
    /// json conversion be unit tested without a plc.
    /// </summary>
    public interface IPlcValueNode
    {
        bool IsArray { get; }
        bool IsStruct { get; }

        /// <summary>Elements of an array node, in index order.</summary>
        IEnumerable<IPlcValueNode> Elements { get; }

        /// <summary>Member names of a struct node, in declaration order.</summary>
        IEnumerable<string> MemberNames { get; }

        bool TryGetMember(string name, out IPlcValueNode member);

        /// <summary>The managed value of a leaf node. Only meaningful when the node is neither array nor struct.</summary>
        object Value { get; }
    }

    /// <summary>
    /// A plc value tree that can be modified in memory before being flushed back to the plc
    /// with a single write.
    /// </summary>
    public interface IMutablePlcValueNode : IPlcValueNode
    {
        bool TrySetMember(string name, object value);
        bool TrySetElement(int index, object value);

        /// <summary>Lower bound of the first array dimension - plc arrays are rarely zero based.</summary>
        int ArrayLowerBound { get; }

        /// <summary>Number of elements of an array node.</summary>
        int ArrayLength { get; }

        bool TryGetMutableMember(string name, out IMutablePlcValueNode member);
        bool TryGetMutableElement(int index, out IMutablePlcValueNode element);
    }
}
