using System.Collections.Generic;

namespace TwinCatAdsTool.Interfaces.Values
{
    /// <summary>
    /// A node of an already-read plc value tree: a struct, an array or a leaf.
    /// Navigating this tree must never cause ads traffic - the whole value is expected to have
    /// been transferred in a single read beforehand. Keeping this behind an interface lets the
    /// json conversion and the restore plan be unit tested without a plc.
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

        /// <summary>
        /// The same leaf as the plc library itself represents it, with the PlcOpen wrapper types
        /// still in place. <see cref="Value"/> unwraps a DT into a DateTime and a TIME into a
        /// TimeSpan, which is what belongs in a backup file; writing one back needs the wrapper
        /// again, and this is the template that says which one.
        /// </summary>
        object NativeValue { get; }

        /// <summary>Lower bound of the first array dimension - plc arrays are rarely zero based.</summary>
        int ArrayLowerBound { get; }
    }
}
