using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.TraceFormat;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;
using Standart.Hash.xxHash;

namespace Microwalk.Analysis.Modules;

[FrameworkModule("control-flow-leakage", "Finds control flow variations on each call stack level.")]
public partial class ControlFlowLeakage : AnalysisStage
{
    private const ulong _addressIdFlagImage = 0x0000_0000_0000_0000;
    private const ulong _addressIdFlagMemory = 0x8000_0000_0000_0000;
    private const ulong _addressIdFlagHeap = 0x4000_0000_0000_0000;
    private const ulong _addressIdFlagsMask = 0xC000_0000_0000_0000;

    /// <summary>
    /// Call stack ID of the root node (base value for call stack ID hashing)
    /// </summary>
    private const ulong _rootNodeCallStackId = 0;

    public override bool SupportsParallelism => false;

    /// <summary>
    /// The output directory for analysis results.
    /// </summary>
    private DirectoryInfo _outputDirectory = null!;

    /// <summary>
    /// MAP file collection for resolving symbol names.
    /// </summary>
    private MapFileCollection _mapFileCollection = null!;

    /// <summary>
    /// Controls whether the call tree should be written to a dump file.
    /// </summary>
    private bool _dumpCallTree;

    /// <summary>
    /// Controls whether the call tree dump should also include memory accesses.
    /// </summary>
    private bool _includeMemoryAccessesInCallTreeDump;

    /// <summary>
    /// Controls whether the call stacks file should include testcase ID trees.
    /// </summary>
    private bool _includeTestcasesInCallStacks;

    /// <summary>
    /// Root node of merged call tree.
    /// </summary>
    private readonly RootNode _rootNode = new();

    /// <summary>
    /// Lookup for formatted image addresses.
    /// </summary>
    private readonly Dictionary<ulong, string> _formattedImageAddresses = new();

    /// <summary>
    /// Lookup for image addresses (used for machine-readable call stack dump, which needs to preserve image offsets).
    /// </summary>
    private readonly Dictionary<ulong, (string imageName, uint offset)> _imageAddresses = new();

    /// <summary>
    /// Lookup for formatted heap/stack addresses.
    /// </summary>
    private readonly Dictionary<ulong, string> _formattedMemoryAddresses = new();

    /// <summary>
    /// Allocation ID which is used for all stack memory accesses which could not be resolved to an allocation.
    /// </summary>
    private const int _unmappedStackAllocationId = 0;

    /// <summary>
    /// Allocation ID which is used for all heap memory accesses which could not be resolved to an allocation.
    /// </summary>
    private const int _unmappedHeapAllocationId = 1;

    /// <summary>
    /// Next ID for an allocation node.
    /// </summary>
    private int _nextSharedAllocationId = 2;

    public override async Task AddTraceAsync(TraceEntity traceEntity)
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

        string logMessagePrefix = $"[analyze:cfl:{traceEntity.Id}]";
        await Logger.LogDebugAsync($"{logMessagePrefix} Processing trace #{traceEntity.Id}");

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
        ulong currentCallStackId = _rootNodeCallStackId;
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
                                await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for call entry");

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
                                await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for branch entry");

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
                                    await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (1) Encountered return entry, but node stack is empty; continuing with root node");
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
                                    await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (2) Encountered return entry, but node stack is empty; continuing with root node");
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
                                    await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (3) Encountered return entry, but node stack is empty; continuing with root node");
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
                                            await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (4) Encountered return entry, but node stack is empty; continuing with root node");
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
                                        await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (5) Encountered return entry, but node stack is empty; continuing with root node");
                                    else
                                    {
                                        (currentNode, successorIndex) = nodeStack.Pop();
                                    }
                                }
                            }
                            else
                            {
                                // Another testcase already hit this branch and ended just before ours, which is weird, but we handle it anyway by creating a dummy split
                                await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for return entry");

                                var splitNode = new SplitNode();
                                var returnNode = new ReturnNode(sourceInstructionId, targetInstructionId);

                                splitNode.Successors.Add(returnNode);
                                splitNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode.SplitSuccessors.Add(splitNode);

                                if(nodeStack.Count == 0)
                                    await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (6) Encountered return entry, but node stack is empty; continuing with root node");
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
                        await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for allocation entry");

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
                            await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Could not find shared stack allocation node S#{memoryAccess.StackAllocationBlockId}, using default unmapped allocation ID");
                        }

                        targetAddressId = _addressIdFlagMemory | ((((ulong)allocationId << 32) | memoryAccess.MemoryRelativeAddress) & ~_addressIdFlagsMask);
                        if(!_formattedMemoryAddresses.ContainsKey(targetAddressId))
                            _formattedMemoryAddresses.Add(targetAddressId, $"S#{(allocationId == _unmappedStackAllocationId ? "?" : allocationId)}+{memoryAccess.MemoryRelativeAddress:x8}");

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
                            await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Could not find shared heap allocation node, using default unmapped allocation ID");
                        }

                        targetAddressId = _addressIdFlagMemory | _addressIdFlagHeap | ((((ulong)allocationId << 32) | memoryAccess.MemoryRelativeAddress) & ~_addressIdFlagsMask);
                        if(!_formattedMemoryAddresses.ContainsKey(targetAddressId))
                            _formattedMemoryAddresses.Add(targetAddressId, $"H#{allocationId}+{memoryAccess.MemoryRelativeAddress:x8}");

                        isWrite = memoryAccess.IsWrite;

                        break;
                    }
                }

                /*
                 * Step 2: Handle individual cases, same as for branches.
                 *
                 * Contrary to branches, we don't split as long as the instruction ID is identical, since memory accesses do not affect control flow.
                 * Instead, a memory access stores a record of all accessed addresses and the respective testcase IDs.
                 */

                // Are there successor nodes from previous testcases?
                if(successorIndex < currentNode.Successors.Count)
                {
                    // Check current successor
                    if(currentNode.Successors[successorIndex] is MemoryAccessNode memoryNode && memoryNode.InstructionId == instructionId)
                    {
                        // The successor matches, check whether our access target is recorded

                        if(memoryNode.Targets.TryGetValue(targetAddressId, out var targetTestcaseIdSet))
                            targetTestcaseIdSet.Add(traceEntity.Id);
                        else
                        {
                            targetTestcaseIdSet = new TestcaseIdSet();
                            targetTestcaseIdSet.Add(traceEntity.Id);
                            memoryNode.Targets.Add(targetAddressId, targetTestcaseIdSet);
                        }

                        ++successorIndex;
                    }
                    else
                    {
                        // Successor does not match, we need to split the current node at this point
                        // This is unlikely, as we should have seen a control flow deviation beforehand. But maybe this is some weird
                        // kind of masked instruction or a conditional move, which sometimes does trigger a memory access and sometimes does not,
                        // so we handle this case anyway.

                        memoryNode = new MemoryAccessNode(instructionId, isWrite);
                        var targetTestcaseIdSet = new TestcaseIdSet();
                        targetTestcaseIdSet.Add(traceEntity.Id);
                        memoryNode.Targets.Add(targetAddressId, targetTestcaseIdSet);

                        var newSplitNode = currentNode.SplitAtSuccessor(successorIndex, traceEntity.Id, memoryNode);

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
                        var memoryNode = new MemoryAccessNode(instructionId, isWrite);
                        var targetTestcaseIdSet = new TestcaseIdSet();
                        targetTestcaseIdSet.Add(traceEntity.Id);
                        memoryNode.Targets.Add(targetAddressId, targetTestcaseIdSet);

                        currentNode.Successors.Add(memoryNode);

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
                                if(memoryNode.Targets.TryGetValue(targetAddressId, out var targetTestcaseIdSet))
                                    targetTestcaseIdSet.Add(traceEntity.Id);
                                else
                                {
                                    targetTestcaseIdSet = new TestcaseIdSet();
                                    targetTestcaseIdSet.Add(traceEntity.Id);
                                    memoryNode.Targets.Add(targetAddressId, targetTestcaseIdSet);
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
                            var memoryNode = new MemoryAccessNode(instructionId, isWrite);
                            var targetTestcaseIdSet = new TestcaseIdSet();
                            targetTestcaseIdSet.Add(traceEntity.Id);
                            memoryNode.Targets.Add(targetAddressId, targetTestcaseIdSet);

                            splitNode.Successors.Add(memoryNode);
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
                        await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for memory access entry");

                        var splitNode = new SplitNode();
                        var memoryNode = new MemoryAccessNode(instructionId, isWrite);
                        var targetTestcaseIdSet = new TestcaseIdSet();
                        targetTestcaseIdSet.Add(traceEntity.Id);
                        memoryNode.Targets.Add(targetAddressId, targetTestcaseIdSet);

                        splitNode.Successors.Add(memoryNode);
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

    public override async Task FinishAsync()
    {
        /*
         * In the first step, we do an iterative depth-first search through the final call tree, and build testcase ID trees for every splitting instruction and call stack.
         * If an instruction causes a split, the associated current testcase ID tree node for the given instruction gets new nodes, where each has the testcase IDs of the currently processed branch.
         * If tree dump generation is enabled, we store the nodes in a dump file while iterating the tree.
         *
         * The results are stored in a consolidated call tree.
         *
         * In the second step, we iterate the consolidated call tree and compute various leakage measures over the individual testcase ID trees.
         */

        string logMessagePrefix = "[analyze:cfl]";
        await Logger.LogInfoAsync($"{logMessagePrefix} Running control flow leakage analysis");

        // Write call tree to text file
        await using var callTreeDumpWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "call-tree-dump.txt")));

        // Store analysis results for each instruction and each call stack
        CallStackNode rootCallStackNode = new CallStackNode { Id = _rootNodeCallStackId };

        // Track call stack entries which we would like to dump later
        HashSet<ulong> interestingCallStackIds = new();

        // Iterate call tree
        Stack<(SplitNode Node, int Level, int? SuccessorIndex, int? SplitSuccessorIndex, CallStackNode CallStackNode, Dictionary<ulong, AnalysisData.TestcaseIdTreeNode> InstructionTestcaseTrees)> nodeStack = new();
        SplitNode currentNode = _rootNode;
        int level = 0;
        string indentation = "";
        int? successorIndex = null;
        int? splitSuccessorIndex = null;
        CallStackNode currentCallStackNode = rootCallStackNode;
        Dictionary<ulong, AnalysisData.TestcaseIdTreeNode> instructionTestcaseTrees = new(); // Indexed by instruction ID
        while(true)
        {
            if(successorIndex == null && splitSuccessorIndex == null)
            {
                // First time encounter of this node

                if(currentNode == _rootNode)
                {
                    if(_dumpCallTree)
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}@root");
                }
                else if(currentNode is CallNode callNode)
                {
                    if(_dumpCallTree)
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}#call {_formattedImageAddresses[callNode.SourceInstructionId]} -> {_formattedImageAddresses[callNode.TargetInstructionId]} (${callNode.CallStackId:X16})");

                    // Switch to corresponding child node
                    var childIndex = currentCallStackNode.Children.FindIndex(c => c.Id == callNode.CallStackId);
                    if(childIndex < 0)
                    {
                        var newCallStackNode = new CallStackNode
                        {
                            Id = callNode.CallStackId,
                            SourceInstructionId = callNode.SourceInstructionId,
                            TargetInstructionId = callNode.TargetInstructionId
                        };
                        currentCallStackNode.Children.Add(newCallStackNode);
                        currentCallStackNode = newCallStackNode;
                    }
                    else
                    {
                        currentCallStackNode = currentCallStackNode.Children[childIndex];
                    }
                }
                else
                {
                    if(_dumpCallTree)
                    {
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}@split");

                        // Testcase IDs
                        // We print testcase IDs only for pure split nodes, to improve readability of the output file
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}  Testcases: {FormatIntegerSequence(currentNode.TestcaseIds.AsEnumerable())} ({currentNode.TestcaseIds.Count} total)");
                    }
                }

                if(_dumpCallTree)
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}  Successors:");

                // Check split successors: If an instruction caused a split, record the testcase IDs of its split successors
                // _Usually_ the splitting instruction should be the same for all split successors.
                // However, we support different split successor instructions here, so we get results even when the traces are a bit weird.
                Dictionary<ulong, (AnalysisData.InstructionType Type, Dictionary<int, TestcaseIdSet> TestcaseIds)> splitSuccessorTestcases = new();
                for(var s = 0; s < currentNode.SplitSuccessors.Count; s++)
                {
                    var splitSuccessor = currentNode.SplitSuccessors[s];
                    var firstInstruction = splitSuccessor.Successors[0];

                    ulong firstInstructionId;
                    AnalysisData.InstructionType firstInstructionType;
                    if(firstInstruction is BranchNode branchNode)
                    {
                        firstInstructionId = branchNode.SourceInstructionId;
                        firstInstructionType = AnalysisData.InstructionType.Jump;
                    }
                    else if(firstInstruction is CallNode callNode)
                    {
                        firstInstructionId = callNode.SourceInstructionId;
                        firstInstructionType = AnalysisData.InstructionType.Call;
                    }
                    else if(firstInstruction is ReturnNode returnNode)
                    {
                        firstInstructionId = returnNode.SourceInstructionId;
                        firstInstructionType = AnalysisData.InstructionType.Return;
                    }
                    else
                        continue; // We only check control flow, as memory access splits are handled differently

                    if(!splitSuccessorTestcases.TryGetValue(firstInstructionId, out var firstInstructionTestcases))
                    {
                        firstInstructionTestcases = (firstInstructionType, new Dictionary<int, TestcaseIdSet>());
                        splitSuccessorTestcases.Add(firstInstructionId, firstInstructionTestcases);
                    }

                    firstInstructionTestcases.TestcaseIds.Add(s, splitSuccessor.TestcaseIds);
                }

                foreach(var (instructionId, (instructionType, testcaseIds)) in splitSuccessorTestcases)
                {
                    if(testcaseIds.Count <= 1)
                        continue;

                    // This instruction appeared more than once, record its hashes in the result list

                    // Get analysis data object for this instruction
                    if(!currentCallStackNode.InstructionAnalysisData.TryGetValue(instructionId, out var analysisData))
                    {
                        analysisData = new AnalysisData
                        {
                            Type = instructionType
                        };
                        currentCallStackNode.InstructionAnalysisData.Add(instructionId, analysisData);
                    }

                    // Record this instruction's entire call stack so it gets included in the analysis result
                    interestingCallStackIds.Add(currentCallStackNode.Id);
                    foreach(var entry in nodeStack)
                        interestingCallStackIds.Add(entry.CallStackNode.Id);

                    // Build testcase ID tree
                    if(instructionTestcaseTrees.TryGetValue(instructionId, out var currentInstructionTestcaseIdTreeNode))
                    {
                        // We already entered a testcase ID tree for the current call stack and instruction, create children for the current node

                        foreach(var (s, testcaseIdSet) in testcaseIds)
                        {
                            currentInstructionTestcaseIdTreeNode.Children.Add(s, new AnalysisData.TestcaseIdTreeNode
                            {
                                TestcaseIds = testcaseIdSet
                            });
                        }
                    }
                    else
                    {
                        // This is the first split for this instruction in the current call subtree
                        var newTestcaseIdTreeRootNode = new AnalysisData.TestcaseIdTreeNode
                        {
                            TestcaseIds = currentNode.TestcaseIds
                        };
                        foreach(var (s, testcaseIdSet) in testcaseIds)
                        {
                            newTestcaseIdTreeRootNode.Children.Add(s, new AnalysisData.TestcaseIdTreeNode
                            {
                                TestcaseIds = testcaseIdSet
                            });
                        }

                        analysisData.TestcaseIdTrees.Add(newTestcaseIdTreeRootNode);

                        instructionTestcaseTrees.Add(instructionId, newTestcaseIdTreeRootNode);
                    }
                }

                successorIndex = 0;
            }

            if(successorIndex != null)
            {
                for(int i = successorIndex.Value; i < currentNode.Successors.Count; ++i)
                {
                    CallTreeNode successorNode = currentNode.Successors[i];
                    if(successorNode is CallNode callNode)
                    {
                        // Dive into split node
                        nodeStack.Push((currentNode, level, i + 1, null, currentCallStackNode, instructionTestcaseTrees));
                        currentNode = callNode;
                        ++level;
                        indentation = new string(' ', 4 * level);
                        successorIndex = null;
                        splitSuccessorIndex = null;
                        instructionTestcaseTrees = new Dictionary<ulong, AnalysisData.TestcaseIdTreeNode>();

                        goto nextIteration;
                    }

                    if(_dumpCallTree && successorNode is ReturnNode returnNode)
                    {
                        // Print node
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}    #return {_formattedImageAddresses[returnNode.SourceInstructionId]} -> {_formattedImageAddresses[returnNode.TargetInstructionId]}");
                    }
                    else if(_dumpCallTree && successorNode is BranchNode branchNode)
                    {
                        // Print node
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}    #branch {_formattedImageAddresses[branchNode.SourceInstructionId]} -> {(branchNode.Taken ? _formattedImageAddresses[branchNode.TargetInstructionId] : "<?> (not taken)")}");
                    }
                    else if(_dumpCallTree && _includeMemoryAccessesInCallTreeDump && successorNode is AllocationNode allocationNode)
                    {
                        // Print node
                        if(allocationNode.IsHeap)
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}    #heapalloc H#{allocationNode.Id}, {allocationNode.Size:x8} bytes");
                        else
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}    #stackalloc S#{allocationNode.Id}, {allocationNode.Size:x8} bytes");
                    }
                    else if(successorNode is MemoryAccessNode memoryAccessNode)
                    {
                        // Print node
                        if(_dumpCallTree && _includeMemoryAccessesInCallTreeDump)
                        {
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}    #memory {_formattedImageAddresses[memoryAccessNode.InstructionId]} {(memoryAccessNode.IsWrite ? "writes" : "reads")}");
                            foreach(var targetAddress in memoryAccessNode.Targets)
                            {
                                string formattedTargetAddress = (targetAddress.Key & _addressIdFlagMemory) != 0
                                    ? _formattedMemoryAddresses[targetAddress.Key]
                                    : _formattedImageAddresses[targetAddress.Key];

                                await callTreeDumpWriter.WriteLineAsync($"{indentation}      {formattedTargetAddress} : {FormatIntegerSequence(targetAddress.Value.AsEnumerable())} ({targetAddress.Value.Count} total)");
                            }
                        }

                        // Record access target testcase IDs, if there is more than one
                        if(memoryAccessNode.Targets.Count > 1)
                        {
                            // Get analysis data object for this instruction
                            if(!currentCallStackNode.InstructionAnalysisData.TryGetValue(memoryAccessNode.InstructionId, out var analysisData))
                            {
                                analysisData = new AnalysisData
                                {
                                    Type = AnalysisData.InstructionType.MemoryAccess
                                };
                                currentCallStackNode.InstructionAnalysisData.Add(memoryAccessNode.InstructionId, analysisData);
                            }

                            // We can't really build a testcase ID tree for this instruction, as it does not split the call tree.
                            // So we just create a new node each time the instruction is encountered.
                            var newTestcaseIdRootNode = new AnalysisData.TestcaseIdTreeNode { TestcaseIds = currentNode.TestcaseIds };
                            int targetIndex = 0;
                            foreach(var target in memoryAccessNode.Targets)
                                newTestcaseIdRootNode.Children.Add(targetIndex++, new AnalysisData.TestcaseIdTreeNode { TestcaseIds = target.Value });

                            analysisData.TestcaseIdTrees.Add(newTestcaseIdRootNode);

                            // Record this instruction's entire call stack so it gets included in the analysis result
                            interestingCallStackIds.Add(currentCallStackNode.Id);
                            foreach(var entry in nodeStack)
                                interestingCallStackIds.Add(entry.CallStackNode.Id);
                        }
                    }
                }

                // Done, move to split successors
                if(_dumpCallTree && currentNode.SplitSuccessors.Count > 0) // Only print this when there actually _are_ split successors
                    await callTreeDumpWriter.WriteLineAsync(indentation + "  SplitSuccessors:");
                successorIndex = null;
                splitSuccessorIndex = 0;
            }

            if(splitSuccessorIndex != null)
            {
                // Dive into split node, if there is any
                // No point putting a loop here, as we will immediately move to a child node.
                if(splitSuccessorIndex.Value < currentNode.SplitSuccessors.Count)
                {
                    // Dive into split node
                    nodeStack.Push((currentNode, level, null, splitSuccessorIndex.Value + 1, currentCallStackNode, instructionTestcaseTrees));
                    currentNode = currentNode.SplitSuccessors[splitSuccessorIndex.Value];

                    if(currentNode is CallNode)
                    {
                        // We record an entire new testcase ID tree for each call
                        instructionTestcaseTrees = new();
                    }
                    else
                    {
                        // Switch to appropriate successor child node
                        var newInstructionTestcaseTrees = new Dictionary<ulong, AnalysisData.TestcaseIdTreeNode>();
                        foreach(var (instructionId, testcaseIdNode) in instructionTestcaseTrees)
                        {
                            if(!testcaseIdNode.Children.TryGetValue(splitSuccessorIndex.Value, out var testcaseIdSplitSuccessorNode))
                            {
                                // Create corresponding dummy node
                                testcaseIdSplitSuccessorNode = new AnalysisData.TestcaseIdTreeNode
                                {
                                    IsDummyNode = true,
                                    TestcaseIds = currentNode.TestcaseIds
                                };
                                testcaseIdNode.Children.Add(splitSuccessorIndex.Value, testcaseIdSplitSuccessorNode);
                            }

                            newInstructionTestcaseTrees.Add(instructionId, testcaseIdSplitSuccessorNode);
                        }

                        instructionTestcaseTrees = newInstructionTestcaseTrees;
                    }

                    ++level;
                    indentation = new string(' ', 4 * level);
                    successorIndex = null;
                    splitSuccessorIndex = null;

                    goto nextIteration;
                }

                // Done, move back up to the parent node, if there is any
                if(nodeStack.Count == 0)
                    break;
                (currentNode, level, successorIndex, splitSuccessorIndex, currentCallStackNode, instructionTestcaseTrees) = nodeStack.Pop();
                indentation = new string(' ', 4 * level);
            }

            // Used after switching to a new node, for immediately breaking inner loops and continuing with the outer one
            nextIteration: ;
        }

        await Logger.LogInfoAsync($"{logMessagePrefix} Control flow leakage analysis completed, writing results");

        StringBuilder stringBuilder = new();

        // Compute some constants for leakage measures
        int totalTestcaseCount = _rootNode.TestcaseIds.Count;
        double idealConditionalGuessingEntropy = 0.5 * (totalTestcaseCount + 1);

        (double mean, double standardDeviation) ComputeConditionalGuessingEntropyScore((double mean, double standardDeviation) entropy) =>
            (100 - 100 * (entropy.mean - 1) / (idealConditionalGuessingEntropy - 1), 100 * entropy.standardDeviation / (idealConditionalGuessingEntropy - 1));

        // Writer for human-readable, formatted call stack
        await using var formattedCallStacksWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "call-stacks.txt")));

        // Write for machine-readable call stack
        await using var callStacksWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "call-stacks.json")));
        await callStacksWriter.WriteAsync("{\"CallStack\":[");

        // Write call stacks
        await Logger.LogInfoAsync($"{logMessagePrefix} Writing analysis result");
        Stack<(int Level, CallStackNode Node, int ChildIndex)> callStack = new();
        int currentChildIndex = 0;
        currentCallStackNode = rootCallStackNode;
        level = -1; // Ignore root node for indentation
        indentation = "";
        while(true)
        {
            bool nodeIsInteresting = interestingCallStackIds.Contains(currentCallStackNode.Id);

            if(nodeIsInteresting && currentChildIndex == 0 && currentCallStackNode.Id != _rootNodeCallStackId)
            {
                // Write call stack entry info
                await formattedCallStacksWriter.WriteLineAsync($"{indentation}{_formattedImageAddresses[currentCallStackNode.SourceInstructionId]} -> {_formattedImageAddresses[currentCallStackNode.TargetInstructionId]} (${currentCallStackNode.Id:X16})");
                await callStacksWriter.WriteAsync($"{{" +
                                                  $"\"CallStackId\":\"{currentCallStackNode.Id:X16}\"," +
                                                  $"\"SourceInstructionImageName\":\"{_imageAddresses[currentCallStackNode.SourceInstructionId].imageName}\"," +
                                                  $"\"SourceInstructionOffset\":{_imageAddresses[currentCallStackNode.SourceInstructionId].offset}," +
                                                  $"\"SourceInstructionFormatted\":\"{_formattedImageAddresses[currentCallStackNode.SourceInstructionId]}\"," +
                                                  $"\"TargetInstructionImageName\":\"{_imageAddresses[currentCallStackNode.TargetInstructionId].imageName}\"," +
                                                  $"\"TargetInstructionOffset\":{_imageAddresses[currentCallStackNode.TargetInstructionId].offset}," +
                                                  $"\"TargetInstructionFormatted\":\"{_formattedImageAddresses[currentCallStackNode.TargetInstructionId]}\"," +
                                                  $"\"LeakageEntries\":["
                );

                // Write analysis results
                foreach(var analysisResult in currentCallStackNode.InstructionAnalysisData)
                {
                    string instructionTypeName = analysisResult.Value.Type switch
                    {
                        AnalysisData.InstructionType.Call => "call",
                        AnalysisData.InstructionType.Return => "return",
                        AnalysisData.InstructionType.Jump => "jump",
                        AnalysisData.InstructionType.MemoryAccess => "memory access",
                        _ => throw new Exception("Unexpected instruction type")
                    };
                    await formattedCallStacksWriter.WriteLineAsync($"{indentation}  [L] {_formattedImageAddresses[analysisResult.Key]} ({instructionTypeName})");
                    await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Number of calls: {analysisResult.Value.TestcaseIdTrees.Count}");
                    await callStacksWriter.WriteAsync($"{{" +
                                                      $"\"ImageName\":\"{_imageAddresses[analysisResult.Key].imageName}\"," +
                                                      $"\"Offset\":{_imageAddresses[analysisResult.Key].offset}," +
                                                      $"\"Type\":\"{instructionTypeName}\"," +
                                                      $"\"NumberOfCalls\":{analysisResult.Value.TestcaseIdTrees.Count},"
                    );

                    // Compute measures
                    List<int> treeDepths = new();
                    List<double> mutualInformations = new();
                    List<double> conditionalGuessingEntropies = new();
                    List<double> minConditionalGuessingEntropies = new();
                    foreach(var testcaseIdTree in analysisResult.Value.TestcaseIdTrees)
                    {
                        List<int> leafTestcaseCounts = new();
                        testcaseIdTree.Measure(leafTestcaseCounts, out var treeDepth);

                        // Conditional guessing entropy for leaves
                        double mutualInformation = 0;
                        long conditionalGuessingEntropy = 0;
                        long minimumConditionalGuessingEntropy = long.MaxValue;
                        foreach(var leaf in leafTestcaseCounts)
                        {
                            mutualInformation += leaf * Math.Log2((double)totalTestcaseCount / leaf);

                            // We want to get the remaining guessing entropy for the testcases X' ⊂ X in this leaf
                            // Since those are uniformly distributed ( Pr[X'=x] = 1/n ), we can use the Gaussian sum formula
                            // to simplify the term: G[X'] = Σ[k= 1...|X'|] k * 1/|X'| = (|X'| + 1) / 2
                            //
                            // The division by 2 is moved outside the loop, in order to improve numeric stability
                            long gX2 = leaf + 1;
                            conditionalGuessingEntropy += leaf * gX2;

                            if(gX2 < minimumConditionalGuessingEntropy)
                                minimumConditionalGuessingEntropy = gX2;
                        }

                        mutualInformations.Add(mutualInformation / totalTestcaseCount);
                        conditionalGuessingEntropies.Add((0.5 * conditionalGuessingEntropy) / totalTestcaseCount);
                        minConditionalGuessingEntropies.Add(0.5 * minimumConditionalGuessingEntropy);
                        treeDepths.Add(treeDepth);
                    }

                    // Print individual measures
                    {
                        var subtreeDepth = ComputeMean(treeDepths.Select(v => (double)v));
                        await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Tree depth: {subtreeDepth.mean:F2} +/- {subtreeDepth.standardDeviation:F2}, min {treeDepths.Min()}, max {treeDepths.Max()}");
                        await callStacksWriter.WriteAsync($"\"TreeDepth\":{{" +
                                                          $"\"Mean\":{subtreeDepth.mean:F2}," +
                                                          $"\"StandardDeviation\":{subtreeDepth.standardDeviation:F2}," +
                                                          $"\"Minimum\":{treeDepths.Min()}," +
                                                          $"\"Maximum\":{treeDepths.Max()}" +
                                                          $"}},"
                        );

                        var mutualInformation = ComputeMean(mutualInformations);
                        await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Mutual information: {mutualInformation.mean:F2} +/- {mutualInformation.standardDeviation:F2} bits, min {mutualInformations.Min():F2} bits, max {mutualInformations.Max():F2} bits");
                        await callStacksWriter.WriteAsync($"\"MutualInformation\":{{" +
                                                          $"\"Mean\":{mutualInformation.mean:F2}," +
                                                          $"\"StandardDeviation\":{mutualInformation.standardDeviation:F2}," +
                                                          $"\"Minimum\":{mutualInformations.Min():F2}," +
                                                          $"\"Maximum\":{mutualInformations.Max():F2}," +
                                                          $"}},"
                        );

                        var conditionalGuessingEntropy = ComputeMean(conditionalGuessingEntropies);
                        var conditionalGuessingEntropyScore = (ComputeConditionalGuessingEntropyScore(conditionalGuessingEntropy));
                        await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Cond. guessing entropy: {conditionalGuessingEntropy.mean:F2} +/- {conditionalGuessingEntropy.standardDeviation:F2}, min {conditionalGuessingEntropies.Min():F2}, max {conditionalGuessingEntropies.Max():F2}, score {conditionalGuessingEntropyScore.mean:F2} +/- {conditionalGuessingEntropyScore.standardDeviation:F2}");
                        await callStacksWriter.WriteAsync($"\"ConditionalGuessingEntropy\":{{" +
                                                          $"\"Mean\":{conditionalGuessingEntropy.mean:F2}," +
                                                          $"\"StandardDeviation\":{conditionalGuessingEntropy.standardDeviation:F2}," +
                                                          $"\"Minimum\":{conditionalGuessingEntropies.Min():F2}," +
                                                          $"\"Maximum\":{conditionalGuessingEntropies.Max():F2}," +
                                                          $"\"Score\":{conditionalGuessingEntropyScore.mean:F2}," +
                                                          $"\"ScoreStandardDeviation\":{conditionalGuessingEntropyScore.standardDeviation:F2}" +
                                                          $"}},"
                        );

                        var minConditionalGuessingEntropy = ComputeMean(minConditionalGuessingEntropies);
                        var minConditionalGuessingEntropyScore = (ComputeConditionalGuessingEntropyScore(minConditionalGuessingEntropy));
                        await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Min. cond. guessing entropy: {minConditionalGuessingEntropy.mean:F2} +/- {minConditionalGuessingEntropy.standardDeviation:F2}, min {minConditionalGuessingEntropies.Min():F2}, max {minConditionalGuessingEntropies.Max():F2}, score {minConditionalGuessingEntropyScore.mean:F2} +/- {minConditionalGuessingEntropyScore.standardDeviation:F2}");
                        await callStacksWriter.WriteAsync($"\"MinimumConditionalGuessingEntropy\":{{" +
                                                          $"\"Mean\":{minConditionalGuessingEntropy.mean:F2}," +
                                                          $"\"StandardDeviation\":{minConditionalGuessingEntropy.standardDeviation:F2}," +
                                                          $"\"Minimum\":{minConditionalGuessingEntropies.Min():F2}," +
                                                          $"\"Maximum\":{minConditionalGuessingEntropies.Max():F2}," +
                                                          $"\"Score\":{minConditionalGuessingEntropyScore.mean:F2}," +
                                                          $"\"ScoreStandardDeviation\":{minConditionalGuessingEntropyScore.standardDeviation:F2}" +
                                                          $"}},"
                        );
                    }

                    if(_includeTestcasesInCallStacks)
                    {
                        await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Testcase IDs:");
                        foreach(var testcaseIdTree in analysisResult.Value.TestcaseIdTrees)
                        {
                            testcaseIdTree.Render(stringBuilder, $"{indentation}     ", $"{indentation}     ", testcaseIdTree.TestcaseIds.Count);
                            await formattedCallStacksWriter.WriteAsync(stringBuilder.ToString());

                            stringBuilder.Clear();
                        }
                    }

                    await callStacksWriter.WriteAsync("},");
                }

                await callStacksWriter.WriteAsync($"]," +
                                                  $"\"Children\":[");
            }

            // Iterate through children
            if(nodeIsInteresting && currentChildIndex < currentCallStackNode.Children.Count)
            {
                callStack.Push((level, currentCallStackNode, currentChildIndex + 1));

                currentCallStackNode = currentCallStackNode.Children[currentChildIndex];
                currentChildIndex = 0;

                ++level;
                indentation = new string(' ', 2 * level);
            }
            else
            {
                // All children are processed
                if(nodeIsInteresting && currentChildIndex == currentCallStackNode.Children.Count && currentCallStackNode.Id != _rootNodeCallStackId)
                    await callStacksWriter.WriteAsync("]},");

                // Go back up by one level, if there is any
                if(callStack.Any())
                {
                    (level, currentCallStackNode, currentChildIndex) = callStack.Pop();
                    indentation = level > 0 ? new string(' ', 2 * level) : "";
                }
                else
                    break;
            }
        }

        await callStacksWriter.WriteAsync("]}");
    }

    protected override async Task InitAsync(MappingNode? moduleOptions)
    {
        if(moduleOptions == null)
            throw new ConfigurationException("Missing module configuration.");

        // Extract output path
        string outputDirectoryPath = moduleOptions.GetChildNodeOrDefault("output-directory")?.AsString() ?? throw new ConfigurationException("Missing output directory for analysis results.");
        _outputDirectory = Directory.CreateDirectory(outputDirectoryPath);

        // Load MAP files
        _mapFileCollection = new MapFileCollection(Logger);
        var mapFilesNode = moduleOptions.GetChildNodeOrDefault("map-files");
        if(mapFilesNode is ListNode mapFileListNode)
        {
            foreach(var mapFileNode in mapFileListNode.Children)
                await _mapFileCollection.LoadMapFileAsync(mapFileNode.AsString() ?? throw new ConfigurationException("Invalid node type in map file list."));
        }

        var mapDirectory = moduleOptions.GetChildNodeOrDefault("map-directory")?.AsString();
        if(mapDirectory != null)
        {
            foreach(var mapFile in Directory.EnumerateFiles(mapDirectory, "*.map"))
                await _mapFileCollection.LoadMapFileAsync(mapFile);
        }

        // Dump internal data?
        _dumpCallTree = moduleOptions.GetChildNodeOrDefault("dump-call-tree")?.AsBoolean() ?? false;
        _includeMemoryAccessesInCallTreeDump = moduleOptions.GetChildNodeOrDefault("include-memory-accesses-in-dump")?.AsBoolean() ?? true;
        _includeTestcasesInCallStacks = moduleOptions.GetChildNodeOrDefault("include-testcases-in-call-stacks")?.AsBoolean() ?? true;
    }

    public override Task UnInitAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Formats the given image address and returns a unique ID.
    /// </summary>
    /// <param name="imageFileInfo">Image information for the given address.</param>
    /// <param name="address">Offset of the address relative to the image.</param>
    private ulong StoreFormattedImageAddress(TracePrefixFile.ImageFileInfo imageFileInfo, uint address)
    {
        // Compute key
        ulong key = _addressIdFlagImage | ((((ulong)imageFileInfo.Id << 32) | address) & ~_addressIdFlagsMask);

        // Address already known?
        if(!_formattedImageAddresses.ContainsKey(key))
            _formattedImageAddresses.TryAdd(key, _mapFileCollection.FormatAddress(imageFileInfo, address));
        if(!_imageAddresses.ContainsKey(key))
            _imageAddresses.Add(key, (imageFileInfo.Name, address));

        return key;
    }

    /// <summary>
    /// Formats a sequence of integers in compressed form.
    /// Example:
    ///     1 2 3 4 6 7 8 10
    ///   becomes
    ///     1-4 6-8 10
    /// </summary>
    /// <param name="sequence">Number sequence, in ascending order.</param>
    /// <returns></returns>
    private static string FormatIntegerSequence(IEnumerable<int> sequence)
    {
        StringBuilder result = new();

        // Number of consecutive integers to trigger a merge
        const int consecutiveThreshold = 2;

        bool first = true;
        int consecutiveStart = 0;
        int consecutiveCurrent = 0;
        foreach(var i in sequence)
        {
            if(first)
            {
                // Initialize first sequence
                consecutiveStart = i;
                consecutiveCurrent = i;

                first = false;
            }
            else if(i == consecutiveCurrent + 1)
            {
                // We are still in a sequence
                consecutiveCurrent = i;
            }
            else
            {
                // We left the previous sequence
                // Did it reach the threshold? -> write it in the appropriate format
                if(consecutiveCurrent - consecutiveStart >= consecutiveThreshold)
                    result.Append($"{consecutiveStart}-{consecutiveCurrent} ");
                else
                {
                    // Threshold missed, just write the numbers
                    for(int j = consecutiveStart; j <= consecutiveCurrent; ++j)
                        result.Append($"{j} ");
                }

                // New sequence
                consecutiveStart = i;
                consecutiveCurrent = i;
            }
        }

        // Write remaining elements of last sequence
        if(consecutiveCurrent - consecutiveStart >= consecutiveThreshold)
            result.Append($"{consecutiveStart}-{consecutiveCurrent} ");
        else
        {
            for(int j = consecutiveStart; j <= consecutiveCurrent; ++j)
                result.Append($"{j} ");
        }

        // Remove trailing space
        if(result[^1] == ' ')
            result.Remove(result.Length - 1, 1);

        return result.ToString();
    }

    /// <summary>
    /// Computes the mean and standard deviation of the given list of values.
    /// </summary>
    /// <param name="values">Values.</param>
    /// <returns></returns>
    private static (double mean, double standardDeviation) ComputeMean(IEnumerable<double> values)
    {
        // Welford's method

        double mean = 0.0;
        double sum = 0.0;
        int i = 0;
        foreach(var v in values)
        {
            ++i;

            double delta = v - mean;

            mean += delta / i;
            sum += delta * (v - mean);
        }

        double standardDeviation = 0.0;
        if(i > 1)
            standardDeviation = Math.Sqrt(sum / i);

        return (mean, standardDeviation);
    }

    private class AnalysisData
    {
        public InstructionType Type { get; init; }

        public List<TestcaseIdTreeNode> TestcaseIdTrees { get; } = new();

        public enum InstructionType
        {
            Call,
            Return,
            Jump,
            MemoryAccess
        }

        public class TestcaseIdTreeNode
        {
            /// <summary>
            /// Determines whether this node is a dummy, generated by a tree split from another instruction that is higher up in the tree.
            /// </summary>
            public bool IsDummyNode { get; init; }

            public TestcaseIdSet TestcaseIds { get; init; } = null!;

            /// <summary>
            /// Maps split successor indexes to testcase ID subtrees.
            /// </summary>
            public Dictionary<int, TestcaseIdTreeNode> Children { get; } = new();

            /// <summary>
            /// Caches whether all children have been identified as dummy nodes.
            /// Filled by <see cref="Measure"/>.
            /// </summary>
            private bool _allChildrenAreDummyNodes;

            /// <summary>
            /// Computes various measures over the current node's subtree.
            /// </summary>
            /// <param name="leafTestcaseCounts">Number of testcases per leaf.</param>
            /// <param name="subtreeDepth">Depth of this node's subtree (including the current node).</param>
            public void Measure(List<int> leafTestcaseCounts, out int subtreeDepth)
            {
                // Dive into child nodes
                subtreeDepth = 1;
                _allChildrenAreDummyNodes = true;
                foreach(var child in Children)
                {
                    child.Value.Measure(leafTestcaseCounts, out int childSubtreeDepth);

                    if(!child.Value._allChildrenAreDummyNodes || !child.Value.IsDummyNode)
                    {
                        subtreeDepth = Math.Max(subtreeDepth, 1 + childSubtreeDepth);
                        _allChildrenAreDummyNodes = false;
                    }
                }

                // Measures for non-dummy leaf nodes
                // We also consider nodes as leaves if their subtree only consists of dummy nodes
                if((Children.Count == 0 || _allChildrenAreDummyNodes) && !IsDummyNode)
                {
                    leafTestcaseCounts.Add(TestcaseIds.Count);
                }
            }

            /// <summary>
            /// Writes the current node and its children into the given <see cref="StringBuilder"/>, recursively.
            /// <see cref="Measure"/> needs to be executed first, as this function relies on some of its results.
            /// </summary>
            /// <param name="stringBuilder">String builder.</param>
            /// <param name="indentationFirst">Line prefix and indentation of the first line.</param>
            /// <param name="indentationOther">Line prefix and indentation of all other lines.</param>
            /// <param name="totalCount">Number of testcase IDs of the top most parent node. Used for computing this node's leakage.</param>
            public void Render(StringBuilder stringBuilder, string indentationFirst, string indentationOther, int totalCount)
            {
                if(IsDummyNode)
                {
                    stringBuilder.AppendFormat(CultureInfo.InvariantCulture,
                            "{0}─[M] {1} ({2} total)",
                            indentationFirst,
                            FormatIntegerSequence(TestcaseIds.AsEnumerable()),
                            TestcaseIds.Count)
                        .AppendLine();
                }
                else
                {
                    // Handle leaf nodes separately
                    if(Children.Count == 0 || _allChildrenAreDummyNodes)
                    {
                        stringBuilder.AppendFormat(CultureInfo.InvariantCulture,
                                "{0}─ {1} ({2} total) ({3:F2} bits global leakage)",
                                indentationFirst,
                                FormatIntegerSequence(TestcaseIds.AsEnumerable()),
                                TestcaseIds.Count,
                                Math.Log2((double)totalCount / TestcaseIds.Count))
                            .AppendLine();
                    }
                    else
                    {
                        stringBuilder.AppendFormat(CultureInfo.InvariantCulture,
                                "{0}─ {1} ({2} total)",
                                indentationFirst,
                                FormatIntegerSequence(TestcaseIds.AsEnumerable()),
                                TestcaseIds.Count)
                            .AppendLine();
                    }
                }

                if(!_allChildrenAreDummyNodes)
                {
                    var childrenList = Children.OrderBy(c => c.Key).ToList(); // We need indexed lookup for pretty printing
                    for(int i = 0; i < childrenList.Count; ++i)
                    {
                        var child = childrenList[i];

                        string childIndentationFirst = indentationOther + (i == childrenList.Count - 1 ? "  └" : "  ├");
                        string childIndentationOther = indentationOther + (i == childrenList.Count - 1 ? "   " : "  │");

                        child.Value.Render(stringBuilder, childIndentationFirst, childIndentationOther, totalCount);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Used for producing tree-like call stacks in the final analysis step.
    /// </summary>
    private class CallStackNode
    {
        public ulong Id { get; init; }
        public ulong SourceInstructionId { get; init; }
        public ulong TargetInstructionId { get; init; }

        public List<CallStackNode> Children { get; } = new();

        /// <summary>
        /// Maps instruction IDs to analysis data.
        /// </summary>
        public Dictionary<ulong, AnalysisData> InstructionAnalysisData { get; } = new();
    }
}