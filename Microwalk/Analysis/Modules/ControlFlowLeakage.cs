using System;
using System.Collections.Generic;
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

namespace Microwalk.Analysis.Modules;

[FrameworkModule("control-flow-leakage", "Finds control flow variations on each call stack level.")]
public class ControlFlowLeakage : AnalysisStage
{
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
    /// Lookup for formatted addresses.
    /// </summary>
    private readonly Dictionary<ulong, string> _formattedAddresses = new();

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

        // Run through trace entries
        Stack<(SplitNode node, int successorIndex)> nodeStack = new();
        SplitNode currentNode = _rootNode;
        int successorIndex = 0;
        int traceEntryId = -1;
        foreach(var traceEntry in traceEntity.PreprocessedTraceFile)
        {
            ++traceEntryId;

            // Ignore non-branch entries and non-taken branches
            if(traceEntry.EntryType != TraceEntryTypes.Branch)
                continue;
            
            var branchEntry = (Branch)traceEntry;
            if(!branchEntry.Taken)
                continue;

            // Format addresses
            ulong sourceInstructionId = ((ulong)branchEntry.SourceImageId << 32) | branchEntry.SourceInstructionRelativeAddress;
            ulong targetInstructionId = ((ulong)branchEntry.DestinationImageId << 32) | branchEntry.DestinationInstructionRelativeAddress;
            StoreFormattedAddress(sourceInstructionId, traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[branchEntry.SourceImageId], branchEntry.SourceInstructionRelativeAddress);
            StoreFormattedAddress(targetInstructionId, traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[branchEntry.DestinationImageId], branchEntry.DestinationInstructionRelativeAddress);

            switch(branchEntry.BranchType)
            {
                case Branch.BranchTypes.Call:
                {
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

                            // Copy remaining info from this node over to 1st split node
                            var splitNode1 = new SplitNode();
                            foreach(var testcaseId in currentNode.TestcaseIds)
                                splitNode1.TestcaseIds.Add(testcaseId);
                            splitNode1.Successors.AddRange(currentNode.Successors.Skip(successorIndex));
                            currentNode.Successors.RemoveRange(successorIndex, currentNode.Successors.Count - successorIndex);
                            splitNode1.SplitSuccessors.AddRange(currentNode.SplitSuccessors);
                            currentNode.SplitSuccessors.Clear();

                            // The 2nd split node holds the new, conflicting entry
                            var splitNode2 = new SplitNode();
                            callNode = new CallNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };
                            splitNode2.Successors.Add(callNode);
                            splitNode2.TestcaseIds.Add(traceEntity.Id);

                            currentNode.SplitSuccessors.Add(splitNode1);
                            currentNode.SplitSuccessors.Add(splitNode2);

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
                            var callNode = new CallNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };
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

                                    nodeStack.Push((splitSuccessor, 1)); // We return to the linear part of the split node
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
                                var callNode = new CallNode
                                {
                                    SourceInstructionId = sourceInstructionId,
                                    TargetInstructionId = targetInstructionId
                                };

                                splitNode.Successors.Add(callNode);
                                splitNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode.SplitSuccessors.Add(splitNode);

                                nodeStack.Push((splitNode, 1)); // We return to the linear part of the split node
                                callNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode = callNode;
                                successorIndex = 0;
                            }
                        }
                        else
                        {
                            // Weird, another testcase produced the successors of the current node, but no split successors.
                            // This should not happen, since the current testcase must have shared all its successor entries until now,
                            // and the other testcase had to emit a "return" statement at some point, taking it back up the call tree...
                            // We handle this case anyway, just to be sure, and append a split node.
                            await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for call entry");

                            var splitNode = new SplitNode();
                            var callNode = new CallNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };

                            splitNode.Successors.Add(callNode);
                            splitNode.TestcaseIds.Add(traceEntity.Id);
                            currentNode.SplitSuccessors.Add(splitNode);

                            nodeStack.Push((splitNode, 1)); // We return to the linear part of the split node
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

                            // Copy remaining info from this node over to 1st split node
                            var splitNode1 = new SplitNode();
                            foreach(var testcaseId in currentNode.TestcaseIds)
                                splitNode1.TestcaseIds.Add(testcaseId);
                            splitNode1.Successors.AddRange(currentNode.Successors.Skip(successorIndex));
                            currentNode.Successors.RemoveRange(successorIndex, currentNode.Successors.Count - successorIndex);
                            splitNode1.SplitSuccessors.AddRange(currentNode.SplitSuccessors);
                            currentNode.SplitSuccessors.Clear();

                            // The 2nd split node holds the new, conflicting entry
                            var splitNode2 = new SplitNode();
                            branchNode = new BranchNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };
                            splitNode2.Successors.Add(branchNode);
                            splitNode2.TestcaseIds.Add(traceEntity.Id);

                            currentNode.SplitSuccessors.Add(splitNode1);
                            currentNode.SplitSuccessors.Add(splitNode2);

                            // Continue with new split node
                            branchNode.TestcaseIds.Add(traceEntity.Id);
                            currentNode = splitNode2;
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
                            var branchNode = new BranchNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };
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
                                var branchNode = new BranchNode
                                {
                                    SourceInstructionId = sourceInstructionId,
                                    TargetInstructionId = targetInstructionId
                                };

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
                            // Weird, until now we were aligned with another testcase just fine, but it seemed to have disappeared suddenly - else
                            // we would have seen a return entry and generated a node split. This case should not occur in any sane trace, but we
                            // handle it anyway and generate a node split.
                            await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for branch entry");

                            var splitNode = new SplitNode();
                            var branchNode = new BranchNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };

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
                                (currentNode, successorIndex) = nodeStack.Pop();
                        }
                        else
                        {
                            // Successor does not match, we need to split the current node at this point

                            // Copy remaining info from this node over to 1st split node
                            var splitNode1 = new SplitNode();
                            foreach(var testcaseId in currentNode.TestcaseIds)
                                splitNode1.TestcaseIds.Add(testcaseId);
                            splitNode1.Successors.AddRange(currentNode.Successors.Skip(successorIndex));
                            currentNode.Successors.RemoveRange(successorIndex, currentNode.Successors.Count - successorIndex);
                            splitNode1.SplitSuccessors.AddRange(currentNode.SplitSuccessors);
                            currentNode.SplitSuccessors.Clear();

                            // The 2nd split node holds the new, conflicting entry
                            var splitNode2 = new SplitNode();
                            returnNode = new ReturnNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };
                            splitNode2.Successors.Add(returnNode);
                            splitNode2.TestcaseIds.Add(traceEntity.Id);

                            currentNode.SplitSuccessors.Add(splitNode1);
                            currentNode.SplitSuccessors.Add(splitNode2);

                            returnNode.TestcaseIds.Add(traceEntity.Id);
                            if(nodeStack.Count == 0)
                                await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (2) Encountered return entry, but node stack is empty; continuing with root node");
                            else
                                (currentNode, successorIndex) = nodeStack.Pop();
                        }
                    }
                    else
                    {
                        // We ran out of successor nodes
                        // Check whether another testcase already hit this particular path
                        if(currentNode.TestcaseIds.Count == 1)
                        {
                            // No, this is purely ours. So just append another successor
                            var returnNode = new ReturnNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };
                            currentNode.Successors.Add(returnNode);

                            returnNode.TestcaseIds.Add(traceEntity.Id);
                            if(nodeStack.Count == 0)
                            {
                                await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (3) Encountered return entry, but node stack is empty; continuing with root node");
                                ++successorIndex;
                            }
                            else
                                (currentNode, successorIndex) = nodeStack.Pop();
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
                                        (currentNode, successorIndex) = nodeStack.Pop();

                                    found = true;
                                    break;
                                }
                            }

                            if(!found)
                            {
                                // Add new split successor
                                var splitNode = new SplitNode();
                                var returnNode = new ReturnNode
                                {
                                    SourceInstructionId = sourceInstructionId,
                                    TargetInstructionId = targetInstructionId
                                };

                                splitNode.Successors.Add(returnNode);
                                splitNode.TestcaseIds.Add(traceEntity.Id);
                                currentNode.SplitSuccessors.Add(splitNode);

                                returnNode.TestcaseIds.Add(traceEntity.Id);
                                if(nodeStack.Count == 0)
                                    await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (5) Encountered return entry, but node stack is empty; continuing with root node");
                                else
                                    (currentNode, successorIndex) = nodeStack.Pop();
                            }
                        }
                        else
                        {
                            // Another testcase already hit this branch and ended just before ours.
                            // However, there was no prior return statement/split, which should not happen.
                            // We handle this case anyway, by creating a dummy split
                            await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] Encountered weird case for return entry");

                            var splitNode = new SplitNode();
                            var returnNode = new ReturnNode
                            {
                                SourceInstructionId = sourceInstructionId,
                                TargetInstructionId = targetInstructionId
                            };

                            splitNode.Successors.Add(returnNode);
                            splitNode.TestcaseIds.Add(traceEntity.Id);
                            currentNode.SplitSuccessors.Add(splitNode);

                            returnNode.TestcaseIds.Add(traceEntity.Id);
                            if(nodeStack.Count == 0)
                                await Logger.LogWarningAsync($"{logMessagePrefix} [{traceEntryId}] (6) Encountered return entry, but node stack is empty; continuing with root node");
                            else
                                (currentNode, successorIndex) = nodeStack.Pop();
                        }
                    }

                    break;
                }
            }
        }
    }

    public override async Task FinishAsync()
    {
        // Write entire state into file?
        if(_dumpFullData)
        {
            // Write call tree
            await using var callTreeDumpWriter = new StreamWriter(File.Create(Path.Combine(_outputDirectory.FullName, "call-tree-dump.txt")));
            Stack<(SplitNode Node, int Level, int? SuccessorIndex, int? SplitSuccessorIndex)> nodeStack = new();
            SplitNode currentNode = _rootNode;
            int level = 0;
            string indentation = "";
            int? successorIndex = null;
            int? splitSuccessorIndex = null;
            while(true)
            {
                if(successorIndex == null && splitSuccessorIndex == null)
                {
                    // First time encounter of this node

                    // Name
                    if(currentNode == _rootNode)
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}@root");
                    else if(currentNode is CallNode callNode)
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}#call {_formattedAddresses[callNode.SourceInstructionId]} -> {_formattedAddresses[callNode.TargetInstructionId]}");
                    else
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}@split");

                    // Testcase IDs
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}  Testcases: {FormatIntegerSequence(currentNode.TestcaseIds.OrderBy(t => t))}");

                    // Successors
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}  Successors:");

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
                            nodeStack.Push((currentNode, level, i + 1, null));
                            currentNode = splitNode;
                            ++level;
                            indentation = new string(' ', 4 * level);
                            successorIndex = null;
                            splitSuccessorIndex = null;

                            goto nextIteration;
                        }
                        else if(successorNode is ReturnNode returnNode)
                        {
                            // Print node
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}    #return {_formattedAddresses[returnNode.SourceInstructionId]} -> {_formattedAddresses[returnNode.TargetInstructionId]}");
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}      Testcases: {FormatIntegerSequence(successorNode.TestcaseIds.OrderBy(t => t))}");
                        }
                        else if(successorNode is BranchNode branchNode)
                        {
                            // Print node
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}    #branch {_formattedAddresses[branchNode.SourceInstructionId]} -> {_formattedAddresses[branchNode.TargetInstructionId]}");
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}      Testcases: {FormatIntegerSequence(successorNode.TestcaseIds.OrderBy(t => t))}");
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
                        nodeStack.Push((currentNode, level, null, splitSuccessorIndex.Value + 1));
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
                    (currentNode, level, successorIndex, splitSuccessorIndex) = nodeStack.Pop();
                    indentation = new string(' ', 4 * level);
                }

                // Used after switching to a new node, for immediately breaking inner loops and continuing with the outer one
                nextIteration: ;
            }
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

    private void StoreFormattedAddress(ulong key, TracePrefixFile.ImageFileInfo imageFileInfo, uint address)
    {
        // Address already known?
        if(_formattedAddresses.ContainsKey(key))
            return;

        // Store formatted address
        _formattedAddresses.TryAdd(key, _mapFileCollection.FormatAddress(imageFileInfo, address));
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
    private string FormatIntegerSequence(IOrderedEnumerable<int> sequence)
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
        public HashSet<int> TestcaseIds { get; } = new();
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
    }

    private class CallNode : SplitNode
    {
        public ulong SourceInstructionId { get; init; }
        public ulong TargetInstructionId { get; init; }
    }

    private class RootNode : SplitNode
    {
    }

    private class BranchNode : CallTreeNode
    {
        public ulong SourceInstructionId { get; init; }
        public ulong TargetInstructionId { get; init; }


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
    }
}