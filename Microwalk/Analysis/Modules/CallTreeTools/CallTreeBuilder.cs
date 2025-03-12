using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.TraceFormat;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;
using Standart.Hash.xxHash;

namespace Microwalk.Analysis.Modules.CallTreeTools;

public class CallTreeBuilder(MapFileCollection mapFileCollection, ILogger logger)
{
    /// <summary>
    /// Allocation ID which is used for all stack memory accesses which could not be resolved to an allocation.
    /// </summary>
    private const int _unmappedStackAllocationId = 0;

    /// <summary>
    /// Allocation ID which is used for all heap memory accesses which could not be resolved to an allocation.
    /// </summary>
    private const int _unmappedHeapAllocationId = 1;

    /// <summary>
    /// Root node of merged call tree.
    /// </summary>
    private readonly RootNode _rootNode = new();

    /// <summary>
    /// Lookup for formatted image addresses and uncompressed image info.
    /// The latter is used for the machine-readable result dump, which preserves image offsets for further processing
    /// (e.g., highlighting the respective lines in a source file).
    /// </summary>
    private readonly Dictionary<ulong, (string formatted, string imageName, uint offset)> _imageAddresses = new();

    /// <summary>
    /// Lookup for formatted heap/stack addresses.
    /// </summary>
    private readonly Dictionary<ulong, string> _memoryAddresses = new();

    /// <summary>
    /// Next ID for an allocation node.
    /// </summary>
    private int _nextSharedAllocationId = 2;

    /// <summary>
    /// Adds the given trace into the call tree.
    /// </summary>
    /// <param name="traceEntity">Trace data object.</param>
    /// <param name="logMessagePrefix">Prefix for debug outputs/warnings.</param>
    public async Task AddTraceAsync(TraceEntity traceEntity, string logMessagePrefix)
    {
        /*
         * Runs linearly through a trace and stores it in a radix trie-like call tree structure.
         *
         * Each node has a linear list of _successors_, followed by a tree of _split successors_.
         *
         * The tree branches when
         * a) a call is encountered (one level down);
         * b) a conflict to an existing entry in a successor list is found (split).
         *
         * For each trace entry there are six cases:
         * 1) For the current (existing) tree node, there is already a successor at the current index.
         *    1.1) The successor matches our current entry. -> continue
         *    1.2) The successor does NOT match our current entry. -> split
         * 2) We exhausted the successors of the current node.
         *    2.1) The current node is only used by the current testcase -> append our entry to the successor list and continue
         *    2.2) Go through the list of split successors.
         *       2.2.1) There is a split successor where successors[0] matches our current entry -> continue
         *       2.2.2) There is no matching split successor -> create our own
         *    2.3) "the weird case" The other testcase(s) just disappeared, without providing a "return" entry which we can
         *         conflict with and thus generate a split. Usually this case should not occur, but in case there are horribly
         *         broken traces we produce a warning and then handle them anyway.
         */

        // Input check
        if(traceEntity.PreprocessedTraceFile == null)
            throw new Exception("Preprocessed trace is null. Is the preprocessor stage missing?");
        if(traceEntity.PreprocessedTraceFile.Prefix == null)
            throw new Exception("Preprocessed trace prefix is null. Is the preprocessor stage missing?");

        // Mark our visit at the root node
        _rootNode.TestcaseIds.Add(traceEntity.Id);

        // Buffer for call stack ID computations
        byte[] callStackBuffer = new byte[24]; // hash | source address | target address
        var callStackBufferHash = callStackBuffer.AsMemory(0..8);
        var callStackBufferSource = callStackBuffer.AsMemory(8..16);
        var callStackBufferTarget = callStackBuffer.AsMemory(16..24);

        // Mapping for trace allocation IDs to call tree allocation IDs
        Dictionary<int, int> heapAllocationIdMapping = new();
        Dictionary<int, int> stackAllocationIdMapping = new();

        // Run through trace entries
        Stack<(SplitNode node, int successorIndex)> nodeStack = new();
        Stack<ulong> callStackIds = new();
        SplitNode currentNode = _rootNode;
        int successorIndex = 0;
        ulong currentCallStackId = CallTree.RootNodeCallStackId;
        int traceEntryId = -1;
        var traceEnumerator = traceEntity.PreprocessedTraceFile.GetNonAllocatingEnumeratorWithPrefix();
        while(traceEnumerator.MoveNext())
        {
            var traceEntry = traceEnumerator.Current;
            ++traceEntryId;

            if(traceEntry.EntryType == TraceEntryTypes.Branch)
            {
                var branchEntry = (Branch)traceEntry;

                // Format addresses
                ulong sourceInstructionId = StoreFormattedImageAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[branchEntry.SourceImageId], branchEntry.SourceInstructionRelativeAddress);

                ulong targetInstructionId = 0;
                if(branchEntry.Taken)
                    targetInstructionId = StoreFormattedImageAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[branchEntry.DestinationImageId], branchEntry.DestinationInstructionRelativeAddress);

                switch(branchEntry.BranchType)
                {
                    case Branch.BranchTypes.Call:
                    {
                        // Compute new call stack ID
                        callStackIds.Push(currentCallStackId);
                        BinaryPrimitives.WriteUInt64LittleEndian(callStackBufferHash.Span, currentCallStackId);
                        BinaryPrimitives.WriteUInt64LittleEndian(callStackBufferSource.Span, sourceInstructionId);
                        BinaryPrimitives.WriteUInt64LittleEndian(callStackBufferTarget.Span, targetInstructionId);
                        currentCallStackId = xxHash64.ComputeHash(callStackBuffer, callStackBuffer.Length);

                        // Are there successor nodes from previous testcases?
                        if(successorIndex < currentNode.Successors.Count)
                        {
                            // Check current successor
                            if(currentNode.Successors[successorIndex] is CallNode callNode && callNode.SourceInstructionId == sourceInstructionId && callNode.TargetInstructionId == targetInstructionId)
                            {
                                // The successor matches, we can continue there

                                nodeStack.Push((currentNode, successorIndex + 1)); // The node still has linear history, we want to return there
                                callNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode = callNode;
                                successorIndex = 0;
                            }
                            else
                            {
                                // Successor does not match, we need to split the current node at this point

                                callNode = new CallNode(sourceInstructionId, targetInstructionId, currentCallStackId);
                                var newSplitNode = currentNode.SplitAtSuccessor(successorIndex, traceEntity.Id, callNode);

                                nodeStack.Push((newSplitNode, 1)); // Return to split node and keep filling its successors
                                callNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode = callNode;
                                successorIndex = 0;
                            }
                        }
                        else
                        {
                            // We ran out of successor nodes
                            // Check whether another testcase already hit this particular path
                            if(currentNode.TestcaseIds.Count == 1)
                            {
                                // No, this is purely ours. So just append another successor
                                var callNode = new CallNode(sourceInstructionId, targetInstructionId, currentCallStackId);
                                currentNode.Successors.Add(callNode);

                                nodeStack.Push((currentNode, successorIndex + 1)); // The node still has linear history, we want to return there
                                callNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode = callNode;
                                successorIndex = 0;
                            }
                            else if(currentNode.SplitSuccessors.Count > 0)
                            {
                                // Is there a split successor that matches?
                                bool found = false;
                                foreach(var splitSuccessor in currentNode.SplitSuccessors)
                                {
                                    if(splitSuccessor.Successors[0] is CallNode callNode && callNode.SourceInstructionId == sourceInstructionId && callNode.TargetInstructionId == targetInstructionId)
                                    {
                                        // The split successor matches, we can continue there

                                        splitSuccessor.TestcaseIds.Add(traceEntity.Id);

                                        nodeStack.Push((splitSuccessor, 1)); // Return to split node and keep going through its successors
                                        callNode.TestcaseIds.Add(traceEntity.Id);
                                        currentNode = callNode;
                                        successorIndex = 0;

                                        found = true;
                                        break;
                                    }
                                }

                                if(!found)
                                {
                                    // Add new split successor
                                    var splitNode = new SplitNode();
                                    var callNode = new CallNode(sourceInstructionId, targetInstructionId, currentCallStackId);

                                    splitNode.Successors.Add(callNode);
                                    splitNode.TestcaseIds.Add(traceEntity.Id);
                                    currentNode.SplitSuccessors.Add(splitNode);

                                    nodeStack.Push((splitNode, 1)); // Return to split node and keep filling its successors
                                    callNode.TestcaseIds.Add(traceEntity.Id);
                                    currentNode = callNode;
                                    successorIndex = 0;
                                }
                            }
                            else
                            {
                                // Another testcase already hit this branch and ended just before ours, which is weird, but we handle it anyway by creating a dummy split
                                await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for call entry");

                                var splitNode = new SplitNode();
                                var callNode = new CallNode(sourceInstructionId, targetInstructionId, currentCallStackId);

                                splitNode.Successors.Add(callNode);
                                splitNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode.SplitSuccessors.Add(splitNode);

                                nodeStack.Push((splitNode, 1)); // Return to split node and keep filling its successors
                                callNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode = callNode;
                                successorIndex = 0;
                            }
                        }

                        break;
                    }

                    case Branch.BranchTypes.Jump:
                    {
                        // Are there successor nodes from previous testcases?
                        if(successorIndex < currentNode.Successors.Count)
                        {
                            // Check current successor
                            if(currentNode.Successors[successorIndex] is BranchNode branchNode && branchNode.SourceInstructionId == sourceInstructionId && branchNode.TargetInstructionId == targetInstructionId)
                            {
                                // The successor matches, nothing to do here

                                ++successorIndex;
                            }
                            else
                            {
                                // Successor does not match, we need to split the current node at this point

                                branchNode = new BranchNode(sourceInstructionId, targetInstructionId, branchEntry.Taken);
                                var newSplitNode = currentNode.SplitAtSuccessor(successorIndex, traceEntity.Id, branchNode);

                                // Continue with new split node
                                currentNode = newSplitNode;
                                successorIndex = 1;
                            }
                        }
                        else
                        {
                            // We ran out of successor nodes
                            // Check whether another testcase already hit this particular path
                            if(currentNode.TestcaseIds.Count == 1)
                            {
                                // No, this is purely ours. So just append another successor
                                var branchNode = new BranchNode(sourceInstructionId, targetInstructionId, branchEntry.Taken);
                                currentNode.Successors.Add(branchNode);

                                // Next
                                ++successorIndex;
                            }
                            else if(currentNode.SplitSuccessors.Count > 0)
                            {
                                // Is there a split successor that matches?
                                bool found = false;
                                foreach(var splitSuccessor in currentNode.SplitSuccessors)
                                {
                                    if(splitSuccessor.Successors[0] is BranchNode branchNode && branchNode.SourceInstructionId == sourceInstructionId && branchNode.TargetInstructionId == targetInstructionId)
                                    {
                                        // The split successor matches, we can continue there

                                        splitSuccessor.TestcaseIds.Add(traceEntity.Id);

                                        currentNode = splitSuccessor;
                                        successorIndex = 1;

                                        found = true;
                                        break;
                                    }
                                }

                                if(!found)
                                {
                                    // Add new split successor
                                    var splitNode = new SplitNode();
                                    var branchNode = new BranchNode(sourceInstructionId, targetInstructionId, branchEntry.Taken);

                                    splitNode.Successors.Add(branchNode);
                                    splitNode.TestcaseIds.Add(traceEntity.Id);
                                    currentNode.SplitSuccessors.Add(splitNode);

                                    // Continue with new split node
                                    currentNode = splitNode;
                                    successorIndex = 1;
                                }
                            }
                            else
                            {
                                // Another testcase already hit this branch and ended just before ours, which is weird, but we handle it anyway by creating a dummy split
                                await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for branch entry");

                                var splitNode = new SplitNode();
                                var branchNode = new BranchNode(sourceInstructionId, targetInstructionId, branchEntry.Taken);

                                splitNode.Successors.Add(branchNode);
                                splitNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode.SplitSuccessors.Add(splitNode);

                                // Continue with new split node
                                currentNode = splitNode;
                                successorIndex = 1;
                            }
                        }

                        break;
                    }

                    case Branch.BranchTypes.Return:
                    {
                        // Are there successor nodes from previous testcases?
                        if(successorIndex < currentNode.Successors.Count)
                        {
                            // Check current successor
                            if(currentNode.Successors[successorIndex] is ReturnNode returnNode && returnNode.SourceInstructionId == sourceInstructionId && returnNode.TargetInstructionId == targetInstructionId)
                            {
                                // The successor matches, we don't need to do anything here

                                if(nodeStack.Count == 0)
                                {
                                    await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (1) Encountered return entry, but node stack is empty; continuing with root node");
                                    ++successorIndex;
                                }
                                else
                                {
                                    (currentNode, successorIndex) = nodeStack.Pop();
                                }
                            }
                            else
                            {
                                // Successor does not match, we need to split the current node at this point

                                returnNode = new ReturnNode(sourceInstructionId, targetInstructionId);
                                currentNode.SplitAtSuccessor(successorIndex, traceEntity.Id, returnNode);

                                if(nodeStack.Count == 0)
                                    await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (2) Encountered return entry, but node stack is empty; continuing with root node");
                                else
                                {
                                    (currentNode, successorIndex) = nodeStack.Pop();
                                }
                            }
                        }
                        else
                        {
                            // We ran out of successor nodes
                            // Check whether another testcase already hit this particular path
                            if(currentNode.TestcaseIds.Count == 1)
                            {
                                // No, this is purely ours. So just append another successor
                                var returnNode = new ReturnNode(sourceInstructionId, targetInstructionId);
                                currentNode.Successors.Add(returnNode);

                                if(nodeStack.Count == 0)
                                {
                                    await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (3) Encountered return entry, but node stack is empty; continuing with root node");
                                    ++successorIndex;
                                }
                                else
                                {
                                    (currentNode, successorIndex) = nodeStack.Pop();
                                }
                            }
                            else if(currentNode.SplitSuccessors.Count > 0)
                            {
                                // Is there a split successor that matches?
                                bool found = false;
                                foreach(var splitSuccessor in currentNode.SplitSuccessors)
                                {
                                    if(splitSuccessor.Successors[0] is ReturnNode returnNode && returnNode.SourceInstructionId == sourceInstructionId && returnNode.TargetInstructionId == targetInstructionId)
                                    {
                                        // The split successor matches, we don't have to do anything here

                                        splitSuccessor.TestcaseIds.Add(traceEntity.Id);

                                        if(nodeStack.Count == 0)
                                            await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (4) Encountered return entry, but node stack is empty; continuing with root node");
                                        else
                                        {
                                            (currentNode, successorIndex) = nodeStack.Pop();
                                        }

                                        found = true;
                                        break;
                                    }
                                }

                                if(!found)
                                {
                                    // Add new split successor
                                    var splitNode = new SplitNode();
                                    var returnNode = new ReturnNode(sourceInstructionId, targetInstructionId);

                                    splitNode.Successors.Add(returnNode);
                                    splitNode.TestcaseIds.Add(traceEntity.Id);
                                    currentNode.SplitSuccessors.Add(splitNode);

                                    if(nodeStack.Count == 0)
                                        await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (5) Encountered return entry, but node stack is empty; continuing with root node");
                                    else
                                    {
                                        (currentNode, successorIndex) = nodeStack.Pop();
                                    }
                                }
                            }
                            else
                            {
                                // Another testcase already hit this branch and ended just before ours, which is weird, but we handle it anyway by creating a dummy split
                                await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for return entry");

                                var splitNode = new SplitNode();
                                var returnNode = new ReturnNode(sourceInstructionId, targetInstructionId);

                                splitNode.Successors.Add(returnNode);
                                splitNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode.SplitSuccessors.Add(splitNode);

                                if(nodeStack.Count == 0)
                                    await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (6) Encountered return entry, but node stack is empty; continuing with root node");
                                else
                                {
                                    (currentNode, successorIndex) = nodeStack.Pop();
                                }
                            }
                        }

                        // Restore call stack ID
                        // We do not emit an extra warning when the call stack is empty, as the node stack is already checked
                        if(callStackIds.Count > 0)
                            currentCallStackId = callStackIds.Pop();

                        break;
                    }
                }
            }
            else if(traceEntry.EntryType is TraceEntryTypes.HeapAllocation or TraceEntryTypes.StackAllocation)
            {
                /*
                 * Step 1: Extract allocation data
                 */

                int id = 0;
                uint size = 0;
                bool isHeap = false;
                switch(traceEntry.EntryType)
                {
                    case TraceEntryTypes.HeapAllocation:
                    {
                        var alloc = (HeapAllocation)traceEntry;

                        id = alloc.Id;
                        size = alloc.Size;
                        isHeap = true;

                        break;
                    }

                    case TraceEntryTypes.StackAllocation:
                    {
                        var alloc = (StackAllocation)traceEntry;

                        id = alloc.Id;
                        size = alloc.Size;
                        isHeap = false;

                        break;
                    }
                }

                /*
                 * Step 2: Handle individual cases, same as for branches.
                 *
                 * We split when the allocation size differs; else we re-use existing allocation nodes, and map all subsequent memory accesses
                 * to its unique ID.
                 */

                var allocationIdMapping = isHeap ? heapAllocationIdMapping : stackAllocationIdMapping;

                // Are there successor nodes from previous testcases?
                if(successorIndex < currentNode.Successors.Count)
                {
                    // Check current successor
                    if(currentNode.Successors[successorIndex] is AllocationNode allocationNode && allocationNode.Size == size && allocationNode.IsHeap == isHeap)
                    {
                        // The successor matches, nothing to do here

                        allocationIdMapping.Add(id, allocationNode.Id);

                        ++successorIndex;
                    }
                    else
                    {
                        // Successor does not match, we need to split the current node at this point

                        allocationNode = new AllocationNode(_nextSharedAllocationId++, size, isHeap);
                        var newSplitNode = currentNode.SplitAtSuccessor(successorIndex, traceEntity.Id, allocationNode);

                        allocationIdMapping.Add(id, allocationNode.Id);

                        // Continue with new split node
                        currentNode = newSplitNode;
                        successorIndex = 1;
                    }
                }
                else
                {
                    // We ran out of successor nodes
                    // Check whether another testcase already hit this particular path
                    if(currentNode.TestcaseIds.Count == 1)
                    {
                        // No, this is purely ours. So just append another successor
                        var allocationNode = new AllocationNode(_nextSharedAllocationId++, size, isHeap);
                        currentNode.Successors.Add(allocationNode);

                        allocationIdMapping.Add(id, allocationNode.Id);

                        // Next
                        ++successorIndex;
                    }
                    else if(currentNode.SplitSuccessors.Count > 0)
                    {
                        // Is there a split successor that matches?
                        bool found = false;
                        foreach(var splitSuccessor in currentNode.SplitSuccessors)
                        {
                            if(splitSuccessor.Successors[0] is AllocationNode allocationNode && allocationNode.Size == size && allocationNode.IsHeap == isHeap)
                            {
                                // The split successor matches, we can continue there

                                splitSuccessor.TestcaseIds.Add(traceEntity.Id);

                                allocationIdMapping.Add(id, allocationNode.Id);

                                currentNode = splitSuccessor;
                                successorIndex = 1;

                                found = true;
                                break;
                            }
                        }

                        if(!found)
                        {
                            // Add new split successor
                            var splitNode = new SplitNode();
                            var allocationNode = new AllocationNode(_nextSharedAllocationId++, size, isHeap);

                            allocationIdMapping.Add(id, allocationNode.Id);

                            splitNode.Successors.Add(allocationNode);
                            splitNode.TestcaseIds.Add(traceEntity.Id);
                            currentNode.SplitSuccessors.Add(splitNode);

                            // Continue with new split node
                            currentNode = splitNode;
                            successorIndex = 1;
                        }
                    }
                    else
                    {
                        // Another testcase already hit this branch and ended just before ours, which is weird, but we handle it anyway by creating a dummy split
                        await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for allocation entry");

                        var splitNode = new SplitNode();
                        var allocationNode = new AllocationNode(_nextSharedAllocationId++, size, isHeap);

                        allocationIdMapping.Add(id, allocationNode.Id);

                        splitNode.Successors.Add(allocationNode);
                        splitNode.TestcaseIds.Add(traceEntity.Id);
                        currentNode.SplitSuccessors.Add(splitNode);

                        // Continue with new split node
                        currentNode = splitNode;
                        successorIndex = 1;
                    }
                }
            }
            else if(traceEntry.EntryType is TraceEntryTypes.ImageMemoryAccess or TraceEntryTypes.StackMemoryAccess or TraceEntryTypes.HeapMemoryAccess)
            {
                /*
                 * Step 1: Extract memory access info
                 */

                // Extract and format address information
                ulong instructionId = 0;
                ulong targetAddressId = 0;
                bool isWrite = false;
                switch(traceEntry.EntryType)
                {
                    case TraceEntryTypes.ImageMemoryAccess:
                    {
                        var memoryAccess = (ImageMemoryAccess)traceEntry;

                        instructionId = StoreFormattedImageAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[memoryAccess.InstructionImageId], memoryAccess.InstructionRelativeAddress);
                        targetAddressId = StoreFormattedImageAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[memoryAccess.MemoryImageId], memoryAccess.MemoryRelativeAddress);

                        isWrite = memoryAccess.IsWrite;

                        break;
                    }

                    case TraceEntryTypes.StackMemoryAccess:
                    {
                        var memoryAccess = (StackMemoryAccess)traceEntry;

                        instructionId = StoreFormattedImageAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[memoryAccess.InstructionImageId], memoryAccess.InstructionRelativeAddress);

                        // Resolve shared allocation ID
                        int allocationId;
                        if(memoryAccess.StackAllocationBlockId == -1)
                            allocationId = _unmappedStackAllocationId;
                        else if(!stackAllocationIdMapping.TryGetValue(memoryAccess.StackAllocationBlockId, out allocationId))
                        {
                            allocationId = _unmappedStackAllocationId;
                            await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Could not find shared stack allocation node S#{memoryAccess.StackAllocationBlockId}, using default unmapped allocation ID");
                        }

                        targetAddressId = CallTree.AddressIdFlagMemory | ((((ulong)allocationId << 32) | memoryAccess.MemoryRelativeAddress) & ~CallTree.AddressIdFlagsMask);
                        if(!_memoryAddresses.ContainsKey(targetAddressId))
                            _memoryAddresses.Add(targetAddressId, $"S#{(allocationId == _unmappedStackAllocationId ? "?" : allocationId)}+{memoryAccess.MemoryRelativeAddress:x8}");

                        isWrite = memoryAccess.IsWrite;

                        break;
                    }

                    case TraceEntryTypes.HeapMemoryAccess:
                    {
                        var memoryAccess = (HeapMemoryAccess)traceEntry;

                        instructionId = StoreFormattedImageAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[memoryAccess.InstructionImageId], memoryAccess.InstructionRelativeAddress);

                        // Resolve shared allocation ID
                        if(!heapAllocationIdMapping.TryGetValue(memoryAccess.HeapAllocationBlockId, out var allocationId))
                        {
                            allocationId = _unmappedHeapAllocationId;
                            await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Could not find shared heap allocation node, using default unmapped allocation ID");
                        }

                        targetAddressId = CallTree.AddressIdFlagMemory | CallTree.AddressIdFlagHeap | ((((ulong)allocationId << 32) | memoryAccess.MemoryRelativeAddress) & ~CallTree.AddressIdFlagsMask);
                        if(!_memoryAddresses.ContainsKey(targetAddressId))
                            _memoryAddresses.Add(targetAddressId, $"H#{allocationId}+{memoryAccess.MemoryRelativeAddress:x8}");

                        isWrite = memoryAccess.IsWrite;

                        break;
                    }
                }

                /*
                 * Step 2: Handle individual cases, same as for branches.
                 *
                 * Contrary to branches, we don't split the tree as long as the instruction ID is identical, since memory accesses do not affect control flow.
                 * Instead, a split memory access stores a record of all accessed addresses and the respective testcase IDs.
                 */

                // Are there successor nodes from previous testcases?
                if(successorIndex < currentNode.Successors.Count)
                {
                    // Check current successor
                    if(currentNode.Successors[successorIndex] is MemoryAccessNode memoryNode && memoryNode.InstructionId == instructionId)
                    {
                        // The successor matches, check whether our access target is recorded

                        if(memoryNode is SimpleMemoryAccessNode simpleMemoryNode)
                        {
                            if(simpleMemoryNode.TargetAddress != targetAddressId)
                            {
                                var splitMemoryNode = new SplitMemoryAccessNode(memoryNode.InstructionId, memoryNode.IsWrite);
                                splitMemoryNode.Targets.Add(simpleMemoryNode.TargetAddress, currentNode.TestcaseIds.Without(traceEntity.Id));
                                splitMemoryNode.Targets.Add(targetAddressId, new TestcaseIdSet(traceEntity.Id));
                                currentNode.Successors[successorIndex] = splitMemoryNode;
                            }
                        }
                        else if(memoryNode is SplitMemoryAccessNode splitMemoryNode)
                        {
                            if(splitMemoryNode.Targets.TryGetValue(targetAddressId, out var targetTestcaseIdSet))
                                targetTestcaseIdSet.Add(traceEntity.Id);
                            else
                            {
                                targetTestcaseIdSet = new TestcaseIdSet();
                                targetTestcaseIdSet.Add(traceEntity.Id);
                                splitMemoryNode.Targets.Add(targetAddressId, targetTestcaseIdSet);
                            }
                        }

                        ++successorIndex;
                    }
                    else
                    {
                        // Successor does not match, we need to split the current node at this point
                        // This is unlikely, as we should have seen a control flow deviation beforehand. But maybe this is some weird
                        // kind of masked instruction or a conditional move, which sometimes does trigger a memory access and sometimes does not,
                        // so we handle this case anyway.

                        var simpleMemoryNode = new SimpleMemoryAccessNode(instructionId, isWrite, targetAddressId);

                        var newSplitNode = currentNode.SplitAtSuccessor(successorIndex, traceEntity.Id, simpleMemoryNode);

                        // Continue with new split node
                        currentNode = newSplitNode;
                        successorIndex = 1;
                    }
                }
                else
                {
                    // We ran out of successor nodes
                    // Check whether another testcase already hit this particular path
                    if(currentNode.TestcaseIds.Count == 1)
                    {
                        // No, this is purely ours. So just append another successor
                        var simpleMemoryNode = new SimpleMemoryAccessNode(instructionId, isWrite, targetAddressId);

                        currentNode.Successors.Add(simpleMemoryNode);

                        // Next
                        ++successorIndex;
                    }
                    else if(currentNode.SplitSuccessors.Count > 0)
                    {
                        // Is there a split successor that matches?
                        bool found = false;
                        foreach(var splitSuccessor in currentNode.SplitSuccessors)
                        {
                            if(splitSuccessor.Successors[0] is MemoryAccessNode memoryNode && memoryNode.InstructionId == instructionId)
                            {
                                // The split successor matches, we can continue there

                                // Check whether our access target is recorded
                                if(memoryNode is SimpleMemoryAccessNode simpleMemoryNode)
                                {
                                    if(simpleMemoryNode.TargetAddress != targetAddressId)
                                    {
                                        var splitMemoryNode = new SplitMemoryAccessNode(memoryNode.InstructionId, memoryNode.IsWrite);
                                        splitMemoryNode.Targets.Add(simpleMemoryNode.TargetAddress, currentNode.TestcaseIds.Without(traceEntity.Id));
                                        splitMemoryNode.Targets.Add(targetAddressId, new TestcaseIdSet(traceEntity.Id));
                                        splitSuccessor.Successors[0] = splitMemoryNode;
                                    }
                                }
                                else if(memoryNode is SplitMemoryAccessNode splitMemoryNode)
                                {
                                    if(splitMemoryNode.Targets.TryGetValue(targetAddressId, out var targetTestcaseIdSet))
                                        targetTestcaseIdSet.Add(traceEntity.Id);
                                    else
                                    {
                                        targetTestcaseIdSet = new TestcaseIdSet();
                                        targetTestcaseIdSet.Add(traceEntity.Id);
                                        splitMemoryNode.Targets.Add(targetAddressId, targetTestcaseIdSet);
                                    }
                                }

                                splitSuccessor.TestcaseIds.Add(traceEntity.Id);

                                currentNode = splitSuccessor;
                                successorIndex = 1;

                                found = true;
                                break;
                            }
                        }

                        if(!found)
                        {
                            // Add new split successor
                            var splitNode = new SplitNode();
                            var simpleMemoryNode = new SimpleMemoryAccessNode(instructionId, isWrite, targetAddressId);

                            splitNode.Successors.Add(simpleMemoryNode);
                            splitNode.TestcaseIds.Add(traceEntity.Id);
                            currentNode.SplitSuccessors.Add(splitNode);

                            // Continue with new split node
                            currentNode = splitNode;
                            successorIndex = 1;
                        }
                    }
                    else
                    {
                        // Another testcase already hit this branch and ended just before ours, which is weird, but we handle it anyway by creating a dummy split
                        await logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for memory access entry");

                        var splitNode = new SplitNode();
                        var simpleMemoryNode = new SimpleMemoryAccessNode(instructionId, isWrite, targetAddressId);

                        splitNode.Successors.Add(simpleMemoryNode);
                        splitNode.TestcaseIds.Add(traceEntity.Id);
                        currentNode.SplitSuccessors.Add(splitNode);

                        // Continue with new split node
                        currentNode = splitNode;
                        successorIndex = 1;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Formats the given image address and returns a unique ID.
    /// </summary>
    /// <param name="imageFileInfo">Image information for the given address.</param>
    /// <param name="address">Offset of the address relative to the image.</param>
    private ulong StoreFormattedImageAddress(TracePrefixFile.ImageFileInfo imageFileInfo, uint address)
    {
        // Compute key
        ulong key = CallTree.AddressIdFlagImage | ((((ulong)imageFileInfo.Id << 32) | address) & ~CallTree.AddressIdFlagsMask);

        // Address already known?
        if(!_imageAddresses.ContainsKey(key))
            _imageAddresses.Add(key, (mapFileCollection.FormatAddress(imageFileInfo.Id, imageFileInfo.Name, address), imageFileInfo.Name, address));

        return key;
    }

    /// <summary>
    /// Converts the internal state into a <see cref="CallTree"/> object.
    /// </summary>
    public CallTree ProduceCallTree()
    {
        return new CallTree
        {
            Root = _rootNode,
            ImageAddresses = _imageAddresses,
            MemoryAddresses = _memoryAddresses
        };
    }
}