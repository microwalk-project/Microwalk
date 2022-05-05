using System;
using System.Collections.Generic;
using System.Linq;

namespace Microwalk.Analysis.Modules;

public partial class ControlFlowLeakage
{
    private abstract class CallTreeNode
    {
    }

    /// <summary>
    /// Dummy node type that has a number of successor nodes, followed by a split.
    /// </summary>
    private class SplitNode : CallTreeNode
    {
        /// <summary>
        /// Testcase IDs leading to this call tree node.
        /// </summary>
        public TestcaseIdSet TestcaseIds { get; protected init; } = new();
        
        /// <summary>
        /// Successors of this node, in linear order.
        /// </summary>
        public List<CallTreeNode> Successors { get; } = new();

        /// <summary>
        /// Alternative successors of this node, directly following the last node of <see cref="Successors"/>, in no particular order.
        /// </summary>
        public List<SplitNode> SplitSuccessors { get; } = new();

        /// <summary>
        /// Splits the given node at the given successor index.
        /// </summary>
        /// <param name="successorIndex">Index of the successor where the split is created.</param>
        /// <param name="testcaseId">Current testcase ID.</param>
        /// <param name="firstSuccessor">First successor of the new split branch.</param>
        /// <returns></returns>
        public SplitNode SplitAtSuccessor(int successorIndex, int testcaseId, CallTreeNode firstSuccessor)
        {
            // Copy remaining info from this node over to 1st split node
            var splitNode1 = new SplitNode
            {
                TestcaseIds = TestcaseIds.Copy()
            };
            splitNode1.TestcaseIds.Remove(testcaseId);

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
    }

    private class CallNode : SplitNode
    {
        public CallNode(ulong sourceInstructionId, ulong targetInstructionId, ulong callStackId)
        {
            SourceInstructionId = sourceInstructionId;
            TargetInstructionId = targetInstructionId;
            CallStackId = callStackId;
        }

        /// <summary>
        /// Branch instruction ID.
        /// </summary>
        public ulong SourceInstructionId { get; }

        /// <summary>
        /// Target instruction ID.
        /// </summary>
        public ulong TargetInstructionId { get; }

        /// <summary>
        /// ID of the call stack created by this node.
        /// </summary>
        public ulong CallStackId { get; }
    }

    private class RootNode : SplitNode
    {
    }

    private class BranchNode : CallTreeNode
    {
        public BranchNode(ulong sourceInstructionId, ulong targetInstructionId, bool taken)
        {
            SourceInstructionId = sourceInstructionId;
            TargetInstructionId = targetInstructionId;
            Taken = taken;
        }

        /// <summary>
        /// Branch instruction ID.
        /// </summary>
        public ulong SourceInstructionId { get; }

        /// <summary>
        /// Target instruction ID. Only valid if <see cref="Taken"/> is true.
        /// </summary>
        public ulong TargetInstructionId { get; }

        /// <summary>
        /// Denotes whether the branch was taken.
        /// </summary>
        public bool Taken { get; }


        public override bool Equals(object? obj)
        {
            return obj is BranchNode other && Equals(other);
        }

        private bool Equals(BranchNode other)
        {
            return SourceInstructionId == other.SourceInstructionId
                   && TargetInstructionId == other.TargetInstructionId;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(SourceInstructionId, TargetInstructionId);
        }
    }

    private class ReturnNode : BranchNode
    {
        public ReturnNode(ulong sourceInstructionId, ulong targetInstructionId)
            : base(sourceInstructionId, targetInstructionId, true)
        {
        }
    }

    private class MemoryAccessNode : CallTreeNode
    {
        public MemoryAccessNode(ulong instructionId, bool isWrite)
        {
            InstructionId = instructionId;
            IsWrite = isWrite;
        }

        /// <summary>
        /// Instruction ID of the memory access.
        /// </summary>
        public ulong InstructionId { get; }

        /// <summary>
        /// Encoded target address of this memory accessing instruction, and the respective testcases.
        /// </summary>
        public Dictionary<ulong, TestcaseIdSet> Targets { get; } = new();

        public bool IsWrite { get; }
    }

    private class AllocationNode : CallTreeNode
    {
        public AllocationNode(int id, uint size, bool isHeap)
        {
            IsHeap = isHeap;
            Size = size;
            Id = id;
        }

        /// <summary>
        /// Unique allocation ID of this node, which all testcase-specific IDs map to. 
        /// </summary>
        public int Id { get; }

        public bool IsHeap { get; }

        /// <summary>
        /// Allocation size.
        /// </summary>
        public uint Size { get; }
    }
}