using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
public class ControlFlowLeakage : AnalysisStage
{
    private const ulong _addressIdFlagImage = 0x0000_0000_0000_0000;
    private const ulong _addressIdFlagMemory = 0x8000_0000_0000_0000;
    private const ulong _addressIdFlagsMask = 0x8000_0000_0000_0000;

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
    /// Controls whether the entire final state should be written to a dump file.
    /// </summary>
    private bool _dumpFullData = false;

    /// <summary>
    /// Root node of merged call tree.
    /// </summary>
    private readonly RootNode _rootNode = new();

    /// <summary>
    /// Lookup for formatted image addresses.
    /// </summary>
    private readonly Dictionary<ulong, string> _formattedImageAddresses = new();

    /// <summary>
    /// Lookup for formatted heap/stack addresses.
    /// </summary>
    private readonly Dictionary<ulong, string> _formattedMemoryAddresses = new();

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

        // Mark our visit at the root node
        _rootNode.TestcaseIds.Add(traceEntity.Id);

        // Buffer for call stack ID computations
        byte[] callStackBuffer = new byte[24]; // hash | source address | target address
        var callStackBufferHash = callStackBuffer.AsMemory(0..8);
        var callStackBufferSource = callStackBuffer.AsMemory(8..16);
        var callStackBufferTarget = callStackBuffer.AsMemory(16..24);

        // Run through trace entries
        Stack<(SplitNode node, int successorIndex)> nodeStack = new();
        Stack<ulong> callStackIds = new();
        SplitNode currentNode = _rootNode;
        int successorIndex = 0;
        ulong currentCallStackId = _rootNodeCallStackId;
        int traceEntryId = -1;
        foreach(var traceEntry in traceEntity.PreprocessedTraceFile)
        {
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

                                branchNode.TestcaseIds.Add(traceEntity.Id);
                                ++successorIndex;
                            }
                            else
                            {
                                // Successor does not match, we need to split the current node at this point

                                branchNode = new BranchNode(sourceInstructionId, targetInstructionId, branchEntry.Taken);
                                var newSplitNode = currentNode.SplitAtSuccessor(successorIndex, traceEntity.Id, branchNode);

                                // Continue with new split node
                                branchNode.TestcaseIds.Add(traceEntity.Id);
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
                                branchNode.TestcaseIds.Add(traceEntity.Id);
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
                                        branchNode.TestcaseIds.Add(traceEntity.Id);

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
                                    branchNode.TestcaseIds.Add(traceEntity.Id);
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
                                branchNode.TestcaseIds.Add(traceEntity.Id);
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

                                returnNode.TestcaseIds.Add(traceEntity.Id);
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

                                returnNode.TestcaseIds.Add(traceEntity.Id);
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

                                returnNode.TestcaseIds.Add(traceEntity.Id);
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

                                        returnNode.TestcaseIds.Add(traceEntity.Id);
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

                                    returnNode.TestcaseIds.Add(traceEntity.Id);
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

                                returnNode.TestcaseIds.Add(traceEntity.Id);
                                if(nodeStack.Count == 0)
                                    await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (6) Encountered return entry, but node stack is empty; continuing with root node");
                                else
                                {
                                    (currentNode, successorIndex) = nodeStack.Pop();
                                }
                            }
                        }

                        // Restore call stack ID
                        if(nodeStack.Count == 0)
                            await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered return entry, but call stack is empty; continuing with root call stack ID");
                        else
                            currentCallStackId = callStackIds.Pop();

                        break;
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

                        targetAddressId = _addressIdFlagMemory | ((((ulong)memoryAccess.StackAllocationBlockId << 32) | memoryAccess.MemoryRelativeAddress) & ~_addressIdFlagsMask);
                        if(!_formattedMemoryAddresses.ContainsKey(targetAddressId))
                            _formattedMemoryAddresses.Add(targetAddressId, $"S#{(memoryAccess.StackAllocationBlockId == -1 ? "?" : memoryAccess.StackAllocationBlockId)}+{memoryAccess.MemoryRelativeAddress:x8}");

                        isWrite = memoryAccess.IsWrite;

                        break;
                    }

                    case TraceEntryTypes.HeapMemoryAccess:
                    {
                        var memoryAccess = (HeapMemoryAccess)traceEntry;

                        instructionId = StoreFormattedImageAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[memoryAccess.InstructionImageId], memoryAccess.InstructionRelativeAddress);

                        targetAddressId = _addressIdFlagMemory | ((((ulong)memoryAccess.HeapAllocationBlockId << 32) | memoryAccess.MemoryRelativeAddress) & ~_addressIdFlagsMask);
                        if(!_formattedMemoryAddresses.ContainsKey(targetAddressId))
                            _formattedMemoryAddresses.Add(targetAddressId, $"H#{memoryAccess.HeapAllocationBlockId}+{memoryAccess.MemoryRelativeAddress:x8}");

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

                        memoryNode.TestcaseIds.Add(traceEntity.Id);
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
                        memoryNode.TestcaseIds.Add(traceEntity.Id);
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
                        memoryNode.TestcaseIds.Add(traceEntity.Id);
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
                                memoryNode.TestcaseIds.Add(traceEntity.Id);

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
                            memoryNode.TestcaseIds.Add(traceEntity.Id);
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
                        memoryNode.TestcaseIds.Add(traceEntity.Id);
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
         * Runs through the final call tree once and does the following:
         * - write a text representation of each node to a text file (human-readable dump of the entire call tree).                                                                     
         * - record hashes of all nodes which are involved in splits. Output the number of distinct hashes per instruction/call stack ID.
         */

        string logMessagePrefix = "[analyze:cfl]";
        await Logger.LogInfoAsync($"{logMessagePrefix} Running control flow leakage analysis");

        // Write call tree to text file
        await using var callTreeDumpWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "call-tree-dump.txt")));

        // Store analysis results for each instruction and each call stack
        Dictionary<(ulong CallStackId, ulong InstructionId), AnalysisData> instructionAnalysisData = new();

        // Record all call stack entries, so we can stitch together the full stacks later
        Dictionary<ulong, (ulong ParentCallStackId, ulong SourceInstructionId, ulong TargetInstructionId)> callStackEntries = new();

        // Track call stack entries which we would like to dump later
        HashSet<ulong> interestingCallStackIds = new();

        // Iterate call tree
        Stack<(SplitNode Node, int Level, int? SuccessorIndex, int? SplitSuccessorIndex, ulong CallStackId)> nodeStack = new();
        SplitNode currentNode = _rootNode;
        int level = 0;
        string indentation = "";
        int? successorIndex = null;
        int? splitSuccessorIndex = null;
        ulong currentCallStackId = _rootNodeCallStackId;
        while(true)
        {
            if(successorIndex == null && splitSuccessorIndex == null)
            {
                // First time encounter of this node

                if(currentNode == _rootNode)
                {
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}@root");
                }
                else if(currentNode is CallNode callNode)
                {
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}#call {_formattedImageAddresses[callNode.SourceInstructionId]} -> {_formattedImageAddresses[callNode.TargetInstructionId]} (${callNode.CallStackId:X16})");

                    callStackEntries[callNode.CallStackId] = (currentCallStackId, callNode.SourceInstructionId, callNode.TargetInstructionId);
                    currentCallStackId = callNode.CallStackId;
                }
                else
                {
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}@split");

                    // Testcase IDs
                    // We print testcase IDs only for pure split nodes, to improve readability of the output file
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}  Testcases: {FormatIntegerSequence(currentNode.TestcaseIds.AsEnumerable())} ({currentNode.TestcaseIds.Count} total)");
                }

                await callTreeDumpWriter.WriteLineAsync($"{indentation}  Successors:");

                // Check split successors: If an instruction caused a split, record its testcase ID hashes
                Dictionary<ulong, HashSet<ulong>> splitSuccessorHashes = new();
                foreach(var splitSuccessor in currentNode.SplitSuccessors)
                {
                    var firstInstruction = splitSuccessor.Successors[0];

                    ulong firstInstructionId;
                    if(firstInstruction is BranchNode branchNode)
                        firstInstructionId = branchNode.SourceInstructionId;
                    else if(firstInstruction is CallNode callNode)
                        firstInstructionId = callNode.SourceInstructionId;
                    else if(firstInstruction is ReturnNode returnNode)
                        firstInstructionId = returnNode.SourceInstructionId;
                    else
                        continue; // Weird

                    if(!splitSuccessorHashes.TryGetValue(firstInstructionId, out var firstInstructionHashes))
                    {
                        firstInstructionHashes = new HashSet<ulong>();
                        splitSuccessorHashes.Add(firstInstructionId, firstInstructionHashes);
                    }

                    firstInstructionHashes.Add(splitSuccessor.TestcaseIds.GetHash());
                }

                foreach(var splitSuccessorData in splitSuccessorHashes)
                {
                    if(splitSuccessorData.Value.Count > 1)
                    {
                        // This instruction appeared more than once, record its hashes in the result list

                        // Get analysis data object for this instruction
                        if(!instructionAnalysisData.TryGetValue((currentCallStackId, splitSuccessorData.Key), out var analysisData))
                        {
                            analysisData = new AnalysisData();
                            instructionAnalysisData.Add((currentCallStackId, splitSuccessorData.Key), analysisData);
                        }

                        foreach(var hash in splitSuccessorData.Value)
                            analysisData.TestcaseHashes.Add(hash);
                    }
                }

                successorIndex = 0;
            }

            if(successorIndex != null)
            {
                for(int i = successorIndex.Value; i < currentNode.Successors.Count; ++i)
                {
                    CallTreeNode successorNode = currentNode.Successors[i];
                    if(successorNode is SplitNode splitNode)
                    {
                        // Dive into split node
                        nodeStack.Push((currentNode, level, i + 1, null, currentCallStackId));
                        currentNode = splitNode;
                        ++level;
                        indentation = new string(' ', 4 * level);
                        successorIndex = null;
                        splitSuccessorIndex = null;

                        goto nextIteration;
                    }

                    if(successorNode is ReturnNode returnNode)
                    {
                        // Print node
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}    #return {_formattedImageAddresses[returnNode.SourceInstructionId]} -> {_formattedImageAddresses[returnNode.TargetInstructionId]}");
                    }
                    else if(successorNode is BranchNode branchNode)
                    {
                        // Print node
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}    #branch {_formattedImageAddresses[branchNode.SourceInstructionId]} -> {(branchNode.Taken ? _formattedImageAddresses[branchNode.TargetInstructionId] : "<?> (not taken)")}");
                    }
                    else if(successorNode is MemoryAccessNode memoryAccessNode)
                    {
                        // Print node
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}    #memory {_formattedImageAddresses[memoryAccessNode.InstructionId]} {(memoryAccessNode.IsWrite ? "writes" : "reads")}");
                        foreach(var targetAddress in memoryAccessNode.Targets)
                        {
                            string formattedTargetAddress = (targetAddress.Key & _addressIdFlagsMask) == _addressIdFlagMemory
                                ? _formattedMemoryAddresses[targetAddress.Key]
                                : _formattedImageAddresses[targetAddress.Key];

                            await callTreeDumpWriter.WriteLineAsync($"{indentation}      {formattedTargetAddress} : {FormatIntegerSequence(targetAddress.Value.AsEnumerable())} ({targetAddress.Value.Count} total)");
                        }
                    }
                }

                // Done, move to split successors
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
                    nodeStack.Push((currentNode, level, null, splitSuccessorIndex.Value + 1, currentCallStackId));
                    currentNode = currentNode.SplitSuccessors[splitSuccessorIndex.Value];
                    ++level;
                    indentation = new string(' ', 4 * level);
                    successorIndex = null;
                    splitSuccessorIndex = null;

                    goto nextIteration;
                }

                // Done, move back up to the parent node, if there is any
                if(nodeStack.Count == 0)
                    break;
                (currentNode, level, successorIndex, splitSuccessorIndex, currentCallStackId) = nodeStack.Pop();
                indentation = new string(' ', 4 * level);
            }

            // Used after switching to a new node, for immediately breaking inner loops and continuing with the outer one
            nextIteration: ;
        }

        await Logger.LogInfoAsync($"{logMessagePrefix} Control flow leakage analysis completed, writing results");

        // Write instruction analysis results
        await Logger.LogInfoAsync($"{logMessagePrefix} Writing results for instruction analysis");
        await using var instructionResultsWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "instructions.txt")));
        foreach(var instructionResult in instructionAnalysisData.OrderBy(i => i.Key.InstructionId))
        {
            if(instructionResult.Value.TestcaseHashes.Count == 0)
                continue;

            await instructionResultsWriter.WriteLineAsync($"${instructionResult.Key.CallStackId:X16} {_formattedImageAddresses[instructionResult.Key.InstructionId]}");
            await instructionResultsWriter.WriteLineAsync($"  Unique hashes: {instructionResult.Value.TestcaseHashes.Count}");

            interestingCallStackIds.Add(instructionResult.Key.CallStackId);
        }

        // Write call stacks
        await Logger.LogInfoAsync($"{logMessagePrefix} Writing interesting call stacks");
        await using var callStacksWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "call-stacks.txt")));
        foreach(var interestingCallStackId in interestingCallStackIds)
        {
            await callStacksWriter.WriteLineAsync($"${interestingCallStackId:X16}");

            // Collect involved call stack IDs
            List<(ulong CallStackId, ulong SourceInstructionId, ulong TargetInstructionId)> callStack = new();
            ulong curId = interestingCallStackId;
            while(curId != _rootNodeCallStackId)
            {
                var entry = callStackEntries[curId];
                callStack.Add((curId, entry.SourceInstructionId, entry.TargetInstructionId));
                curId = entry.ParentCallStackId;
            }

            // Write call stack
            await callStacksWriter.WriteAsync($"   {new string(' ', 16)}  <root>");
            for(int i = callStack.Count - 1; i >= 0; --i)
            {
                var entry = callStack[i];
                await callStacksWriter.WriteLineAsync($" ... {_formattedImageAddresses[entry.SourceInstructionId]}");
                await callStacksWriter.WriteAsync($"  ${entry.CallStackId:X16}  {_formattedImageAddresses[entry.TargetInstructionId]}");
            }

            await callStacksWriter.WriteLineAsync();
            await callStacksWriter.WriteLineAsync();
        }
    }

    protected override async Task InitAsync(MappingNode? moduleOptions)
    {
        if(moduleOptions == null)
            throw new ConfigurationException("Missing module configuration.");

        // Extract output path
        string outputDirectoryPath = moduleOptions.GetChildNodeOrDefault("output-directory")?.AsString() ?? throw new ConfigurationException("Missing output directory for analysis results.");
        _outputDirectory = new DirectoryInfo(outputDirectoryPath);
        if(!_outputDirectory.Exists)
            _outputDirectory.Create();

        // Load MAP files
        _mapFileCollection = new MapFileCollection(Logger);
        var mapFilesNode = moduleOptions.GetChildNodeOrDefault("map-files");
        if(mapFilesNode is ListNode mapFileListNode)
            foreach(var mapFileNode in mapFileListNode.Children)
                await _mapFileCollection.LoadMapFileAsync(mapFileNode.AsString() ?? throw new ConfigurationException("Invalid node type in map file list."));

        // Dump internal data?
        _dumpFullData = moduleOptions.GetChildNodeOrDefault("dump-full-data")?.AsBoolean() ?? false;
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
    private string FormatIntegerSequence(IEnumerable<int> sequence)
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

    private abstract class CallTreeNode
    {
        /// <summary>
        /// Testcase IDs leading to this call tree node.
        /// </summary>
        public TestcaseIdSet TestcaseIds { get; protected init; } = new();
    }

    /// <summary>
    /// Dummy node type that has a number of successor nodes, followed by a split.
    /// </summary>
    private class SplitNode : CallTreeNode
    {
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

    /// <summary>
    /// Utility class for efficient storage of a testcase ID set.
    /// Assumes that testcase IDs are small and don't have large gaps in between.
    /// </summary>
    /// <remarks>
    /// This class is not thread-safe.
    /// </remarks>
    private class TestcaseIdSet
    {
        private ulong[] _testcaseIdBitField = new ulong[1];

        private static byte[] _hashBuffer = new byte[8];

        private void EnsureArraySize(int id)
        {
            if(id / 64 < _testcaseIdBitField.Length)
                return;

            int newSize = 2 * _testcaseIdBitField.Length;
            while(newSize <= id / 64)
                newSize *= 2;

            // Resize
            ulong[] newBitField = new ulong[newSize];
            _testcaseIdBitField.CopyTo(newBitField, 0);

            _testcaseIdBitField = newBitField;
        }

        /// <summary>
        /// Adds the given testcase ID to this set, if it is not yet included.
        /// </summary>
        /// <param name="id">Testcase ID.</param>
        public void Add(int id)
        {
            EnsureArraySize(id);

            _testcaseIdBitField[id / 64] |= (1ul << (id % 64));
        }

        /// <summary>
        /// Removes the given testcase ID from this set, if it is included.
        /// </summary>
        /// <param name="id">Testcase ID.</param>
        public void Remove(int id)
        {
            EnsureArraySize(id);

            _testcaseIdBitField[id / 64] &= ~(1ul << (id % 64));
        }

        /// <summary>
        /// Returns a deep copy of this set.
        /// </summary>
        public TestcaseIdSet Copy()
        {
            TestcaseIdSet s = new TestcaseIdSet
            {
                _testcaseIdBitField = new ulong[_testcaseIdBitField.Length]
            };
            _testcaseIdBitField.CopyTo(s._testcaseIdBitField, 0);

            return s;
        }

        /// <summary>
        /// Returns the included testcase ID in ascending order.
        /// </summary>
        public IEnumerable<int> AsEnumerable()
        {
            for(int i = 0; i < _testcaseIdBitField.Length; ++i)
            {
                ulong b = _testcaseIdBitField[i];
                for(int j = 0; j < 64; ++j)
                {
                    if((b & 1) != 0)
                        yield return 64 * i + j;

                    b >>= 1;
                }
            }
        }

        /// <summary>
        /// Computes a hash over all included IDs.
        /// </summary>
        public ulong GetHash()
        {
            // Resize shared hash buffer, if necessary
            int byteCount = 8 * _testcaseIdBitField.Length;
            if(_hashBuffer.Length < byteCount)
                _hashBuffer = new byte[byteCount];

            // Copy encoded testcase IDs to hash buffer
            Buffer.BlockCopy(_testcaseIdBitField, 0, _hashBuffer, 0, byteCount);

            return xxHash64.ComputeHash(_hashBuffer, byteCount);
        }

        /// <summary>
        /// Returns the number of IDs contained in this object.
        /// </summary>
        public int Count => _testcaseIdBitField.Sum(BitOperations.PopCount);
    }

    private class AnalysisData
    {
        public HashSet<ulong> TestcaseHashes { get; } = new();
    }
}