using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Microwalk.Analysis.Modules.CallTree;

/// <summary>
/// Call tree node.
/// Currently empty base class.
/// </summary>
public abstract record CallTreeNode;

/// <summary>
/// Dummy node type that has a number of successor nodes, followed by a split.
/// </summary>
[SuppressMessage("ReSharper", "TypeWithSuspiciousEqualityIsUsedInRecord.Global")]
public record SplitNode : CallTreeNode
{
    /// <summary>
    /// Testcase IDs leading to this call tree node.
    /// </summary>
    public TestcaseIdSet TestcaseIds { get; private init; } = new();

    /// <summary>
    /// Successors of this node, in linear order.
    /// </summary>
    public List<CallTreeNode> Successors { get; } = [];

    /// <summary>
    /// Alternative successors of this node, directly following the last node of <see cref="Successors"/>, in no particular order.
    /// </summary>
    public List<SplitNode> SplitSuccessors { get; } = [];

    /// <summary>
    /// Splits the given node at the given successor index and creates two new split nodes at that position:
    /// #1: Testcase IDs and all remaining successors of this node after the given index.
    /// #2: The new conflicting successor and its testcase ID.
    /// </summary>
    /// <param name="successorIndex">Index of the successor where the split is created.</param>
    /// <param name="testcaseId">Current testcase ID.</param>
    /// <param name="firstSuccessor">First successor of the new split branch.</param>
    /// <returns>The newly created split node #2 for the given conflicting testcase.</returns>
    public SplitNode SplitAtSuccessor(int successorIndex, int testcaseId, CallTreeNode firstSuccessor)
    {
        // Copy remaining info from this node over to 1st split node
        var splitNode1 = new SplitNode
        {
            TestcaseIds = TestcaseIds.Copy()
        };
        splitNode1.TestcaseIds.Remove(testcaseId);

        // Copy remaining successors to 1st split node
        splitNode1.Successors.AddRange(Successors.Skip(successorIndex));
        Successors.RemoveRange(successorIndex, Successors.Count - successorIndex);
        splitNode1.SplitSuccessors.AddRange(SplitSuccessors);
        SplitSuccessors.Clear();

        // The 2nd split node holds the new, conflicting entry
        var splitNode2 = new SplitNode();
        splitNode2.Successors.Add(firstSuccessor);
        splitNode2.TestcaseIds.Add(testcaseId);

        SplitSuccessors.Add(splitNode1);
        SplitSuccessors.Add(splitNode2);

        return splitNode2;
    }

    public virtual bool Equals(SplitNode? other)
    {
        throw new NotSupportedException("You should not compare split nodes directly.");
    }

    public override int GetHashCode()
    {
        throw new NotSupportedException("You should not compare split nodes directly.");
    }
}

/// <summary>
/// A function call.
/// This is also a split node, so a new tree branch is opened.
/// </summary>
/// <param name="SourceInstructionId">ID of the branch instruction.</param>
/// <param name="TargetInstructionId">ID of the target instruction.</param>
/// <param name="CallStackId">ID of the call stack created by this node.</param>
public record CallNode(ulong SourceInstructionId, ulong TargetInstructionId, ulong CallStackId) : SplitNode;

/// <summary>
/// Tree root node.
/// This node does not actually represent anything in a particular trace, it just ensures that the trace merging
/// yields a valid tree without needing much specialized logic for handling the trace starts.
/// </summary>
public record RootNode : SplitNode;

/// <summary>
/// A conditional/unconditional branch (but not a call).
/// </summary>
/// <param name="SourceInstructionId">ID of the branch instruction.</param>
/// <param name="TargetInstructionId">ID of the target instruction. Only valid if <see cref="Taken"/> is true.</param>
/// <param name="Taken">True if the branch was taken.</param>
public record BranchNode(ulong SourceInstructionId, ulong TargetInstructionId, bool Taken) : CallTreeNode;

/// <summary>
/// A function return.
/// </summary>
/// <inheritdoc />
public record ReturnNode(ulong SourceInstructionId, ulong TargetInstructionId) : BranchNode(SourceInstructionId, TargetInstructionId, true);

/// <summary>
/// A generic memory access.
/// </summary>
/// <param name="InstructionId">Instruction ID of the memory access.</param>
/// <param name="IsWrite">True, if the access was a store.</param>
public abstract record MemoryAccessNode(ulong InstructionId, bool IsWrite) : CallTreeNode;

/// <summary>
/// A memory access to a given address.
/// </summary>
/// <inheritdoc />
/// <param name="TargetAddress">Accessed memory address.</param>
public record SimpleMemoryAccessNode(ulong InstructionId, bool IsWrite, ulong TargetAddress) : MemoryAccessNode(InstructionId, IsWrite);

/// <summary>
/// A memory access that went to different addresses for some testcases.
/// This node type tracks the testcase IDs for each observed target address.
/// </summary>
/// <inheritdoc />
public record SplitMemoryAccessNode(ulong InstructionId, bool IsWrite) : MemoryAccessNode(InstructionId, IsWrite)
{
    /// <summary>
    /// Encoded target address of this memory accessing instruction, and the respective testcases.
    /// </summary>
    public Dictionary<ulong, TestcaseIdSet> Targets { get; } = new();
}

/// <summary>
/// A memory allocation.
/// </summary>
/// <param name="Id">Unique allocation ID of this node, which all testcase-specific IDs map to. </param>
/// <param name="Size">Allocation size.</param>
/// <param name="IsHeap">Marks a heap allocation.</param>
public record AllocationNode(int Id, uint Size, bool IsHeap) : CallTreeNode;