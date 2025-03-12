using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microwalk.Analysis.Modules.CallTreeTools;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;
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

    private CallTreeBuilder _callTreeBuilder = null!;

    public override async Task AddTraceAsync(TraceEntity traceEntity)
    {
        string logMessagePrefix = $"[analyze:cfl:{traceEntity.Id}]";
        await Logger.LogDebugAsync($"{logMessagePrefix} Processing trace #{traceEntity.Id}");

        await _callTreeBuilder.AddTraceAsync(traceEntity, logMessagePrefix + " [tree]");
    }

    public override async Task FinishAsync()
    {
        /*
         * In the first step, we do an iterative depth-first search through the final call tree, and build testcase ID trees for every splitting instruction and call stack.
         * If an instruction causes a split, the associated current testcase ID tree node for the given instruction gets new nodes, where each has the testcase IDs of the currently processed branch.
         *
         * The results are stored in a consolidated call tree.
         *
         * In the second step, we iterate the consolidated call tree and compute various leakage measures over the individual testcase ID trees.
         */

        string logMessagePrefix = "[analyze:cfl]";
        await Logger.LogInfoAsync($"{logMessagePrefix} Running control flow leakage analysis");

        var callTree = _callTreeBuilder.ProduceCallTree();

        // Write call tree to text file
        if(_dumpCallTree)
        {
            var callTreeDumper = new CallTreeDumper { CallTree = callTree };
            await callTreeDumper.DumpCallTreeAsync(Path.Combine(_outputDirectory.FullName, "call-tree-dump.txt"), _includeMemoryAccessesInCallTreeDump);
        }

        // Store analysis results for each instruction and each call stack
        CallStackNode rootCallStackNode = new CallStackNode { Id = CallTree.RootNodeCallStackId };

        // Track call stack entries which we would like to dump later
        HashSet<ulong> interestingCallStackIds = new();

        // Iterate call tree
        Stack<(SplitNode Node, int? SuccessorIndex, int? SplitSuccessorIndex, CallStackNode CallStackNode, Dictionary<ulong, TestcaseIdTreeNode> InstructionTestcaseTrees)> nodeStack = new();
        SplitNode currentNode = callTree.Root;
        int? successorIndex = null;
        int? splitSuccessorIndex = null;
        CallStackNode currentCallStackNode = rootCallStackNode;
        Dictionary<ulong, TestcaseIdTreeNode> instructionTestcaseTrees = new(); // Indexed by instruction ID
        while(true)
        {
            if(successorIndex == null && splitSuccessorIndex == null)
            {
                // First time encounter of this node
                {
                    if(currentNode is CallNode callNode)
                    {
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
                }

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
                            currentInstructionTestcaseIdTreeNode.Children.Add(s, new TestcaseIdTreeNode
                            {
                                TestcaseIds = testcaseIdSet
                            });
                        }
                    }
                    else
                    {
                        // This is the first split for this instruction in the current call subtree
                        var newTestcaseIdTreeRootNode = new TestcaseIdTreeNode
                        {
                            TestcaseIds = currentNode.TestcaseIds
                        };
                        foreach(var (s, testcaseIdSet) in testcaseIds)
                        {
                            newTestcaseIdTreeRootNode.Children.Add(s, new TestcaseIdTreeNode
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
                        nodeStack.Push((currentNode, i + 1, null, currentCallStackNode, instructionTestcaseTrees));
                        currentNode = callNode;
                        successorIndex = null;
                        splitSuccessorIndex = null;
                        instructionTestcaseTrees = new Dictionary<ulong, TestcaseIdTreeNode>();

                        goto nextIteration;
                    }

                    if(successorNode is MemoryAccessNode memoryAccessNode)
                    {
                        // Record access target testcase IDs, if there is more than one
                        if(memoryAccessNode is SplitMemoryAccessNode splitMemoryAccessNode && splitMemoryAccessNode.Targets.Count > 1)
                        {
                            // Get analysis data object for this instruction
                            if(!currentCallStackNode.InstructionAnalysisData.TryGetValue(splitMemoryAccessNode.InstructionId, out var analysisData))
                            {
                                analysisData = new AnalysisData
                                {
                                    Type = AnalysisData.InstructionType.MemoryAccess
                                };
                                currentCallStackNode.InstructionAnalysisData.Add(splitMemoryAccessNode.InstructionId, analysisData);
                            }

                            // We can't really build a testcase ID tree for this instruction, as it does not split the call tree.
                            // So we just create a new node each time the instruction is encountered.
                            var newTestcaseIdRootNode = new TestcaseIdTreeNode { TestcaseIds = currentNode.TestcaseIds };
                            int targetIndex = 0;
                            foreach(var target in splitMemoryAccessNode.Targets)
                                newTestcaseIdRootNode.Children.Add(targetIndex++, new TestcaseIdTreeNode { TestcaseIds = target.Value });

                            analysisData.TestcaseIdTrees.Add(newTestcaseIdRootNode);

                            // Record this instruction's entire call stack so it gets included in the analysis result
                            interestingCallStackIds.Add(currentCallStackNode.Id);
                            foreach(var entry in nodeStack)
                                interestingCallStackIds.Add(entry.CallStackNode.Id);
                        }
                    }
                }

                // Done, move to split successors
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
                    nodeStack.Push((currentNode, null, splitSuccessorIndex.Value + 1, currentCallStackNode, instructionTestcaseTrees));
                    currentNode = currentNode.SplitSuccessors[splitSuccessorIndex.Value];

                    if(currentNode is CallNode)
                    {
                        // We record an entire new testcase ID tree for each call
                        instructionTestcaseTrees = new();
                    }
                    else
                    {
                        // Switch to appropriate successor child node
                        var newInstructionTestcaseTrees = new Dictionary<ulong, TestcaseIdTreeNode>();
                        foreach(var (instructionId, testcaseIdNode) in instructionTestcaseTrees)
                        {
                            if(!testcaseIdNode.Children.TryGetValue(splitSuccessorIndex.Value, out var testcaseIdSplitSuccessorNode))
                            {
                                // Create corresponding dummy node
                                testcaseIdSplitSuccessorNode = new TestcaseIdTreeNode
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

                    successorIndex = null;
                    splitSuccessorIndex = null;

                    goto nextIteration;
                }

                // Done, move back up to the parent node, if there is any
                if(nodeStack.Count == 0)
                    break;
                (currentNode, successorIndex, splitSuccessorIndex, currentCallStackNode, instructionTestcaseTrees) = nodeStack.Pop();
            }

            // Used after switching to a new node, for immediately breaking inner loops and continuing with the outer one
            nextIteration: ;
        }

        await Logger.LogInfoAsync($"{logMessagePrefix} Control flow leakage analysis completed, writing results");

        StringBuilder stringBuilder = new();

        // Compute some constants for leakage measures
        int totalTestcaseCount = callTree.Root.TestcaseIds.Count;
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
        int level = -1; // Ignore root node for indentation
        string indentation = "";
        HashSet<ulong> uniqueLeakingInstructions = new();
        int totalNumberLeakages = 0;
        while(true)
        {
            bool nodeIsInteresting = interestingCallStackIds.Contains(currentCallStackNode.Id);

            if(nodeIsInteresting && currentChildIndex == 0 && currentCallStackNode.Id != CallTree.RootNodeCallStackId)
            {
                // Write call stack entry info
                await formattedCallStacksWriter.WriteLineAsync($"{indentation}{callTree.ImageAddresses[currentCallStackNode.SourceInstructionId].formatted} -> {callTree.ImageAddresses[currentCallStackNode.TargetInstructionId].formatted} (${currentCallStackNode.Id:X16})");
                await callStacksWriter.WriteAsync($"{{" +
                                                  $"\"CallStackId\":\"{currentCallStackNode.Id:X16}\"," +
                                                  $"\"SourceInstructionImageName\":\"{callTree.ImageAddresses[currentCallStackNode.SourceInstructionId].imageName}\"," +
                                                  $"\"SourceInstructionOffset\":{callTree.ImageAddresses[currentCallStackNode.SourceInstructionId].offset}," +
                                                  $"\"SourceInstructionFormatted\":\"{callTree.ImageAddresses[currentCallStackNode.SourceInstructionId].formatted}\"," +
                                                  $"\"TargetInstructionImageName\":\"{callTree.ImageAddresses[currentCallStackNode.TargetInstructionId].imageName}\"," +
                                                  $"\"TargetInstructionOffset\":{callTree.ImageAddresses[currentCallStackNode.TargetInstructionId].offset}," +
                                                  $"\"TargetInstructionFormatted\":\"{callTree.ImageAddresses[currentCallStackNode.TargetInstructionId].formatted}\"," +
                                                  $"\"LeakageEntries\":["
                );

                // Write analysis results
                foreach(var analysisResult in currentCallStackNode.InstructionAnalysisData)
                {
                    ++totalNumberLeakages;
                    uniqueLeakingInstructions.Add(analysisResult.Key);

                    string instructionTypeName = analysisResult.Value.Type switch
                    {
                        AnalysisData.InstructionType.Call => "call",
                        AnalysisData.InstructionType.Return => "return",
                        AnalysisData.InstructionType.Jump => "jump",
                        AnalysisData.InstructionType.MemoryAccess => "memory access",
                        _ => throw new Exception("Unexpected instruction type")
                    };
                    await formattedCallStacksWriter.WriteLineAsync($"{indentation}  [L] {callTree.ImageAddresses[analysisResult.Key].formatted} ({instructionTypeName})");
                    await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Number of calls: {analysisResult.Value.TestcaseIdTrees.Count}");
                    await callStacksWriter.WriteAsync($"{{" +
                                                      $"\"ImageName\":\"{callTree.ImageAddresses[analysisResult.Key].imageName}\"," +
                                                      $"\"Offset\":{callTree.ImageAddresses[analysisResult.Key].offset}," +
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
                        var subtreeDepth = Utilities.ComputeMean(treeDepths.Select(v => (double)v));
                        await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Tree depth: {subtreeDepth.mean:F2} +/- {subtreeDepth.standardDeviation:F2}, min {treeDepths.Min()}, max {treeDepths.Max()}");
                        await callStacksWriter.WriteAsync($"\"TreeDepth\":{{" +
                                                          $"\"Mean\":{subtreeDepth.mean:F2}," +
                                                          $"\"StandardDeviation\":{subtreeDepth.standardDeviation:F2}," +
                                                          $"\"Minimum\":{treeDepths.Min()}," +
                                                          $"\"Maximum\":{treeDepths.Max()}" +
                                                          $"}},"
                        );

                        var mutualInformation = Utilities.ComputeMean(mutualInformations);
                        await formattedCallStacksWriter.WriteLineAsync($"{indentation}    - Mutual information: {mutualInformation.mean:F2} +/- {mutualInformation.standardDeviation:F2} bits, min {mutualInformations.Min():F2} bits, max {mutualInformations.Max():F2} bits");
                        await callStacksWriter.WriteAsync($"\"MutualInformation\":{{" +
                                                          $"\"Mean\":{mutualInformation.mean:F2}," +
                                                          $"\"StandardDeviation\":{mutualInformation.standardDeviation:F2}," +
                                                          $"\"Minimum\":{mutualInformations.Min():F2}," +
                                                          $"\"Maximum\":{mutualInformations.Max():F2}," +
                                                          $"}},"
                        );

                        var conditionalGuessingEntropy = Utilities.ComputeMean(conditionalGuessingEntropies);
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

                        var minConditionalGuessingEntropy = Utilities.ComputeMean(minConditionalGuessingEntropies);
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
                if(nodeIsInteresting && currentChildIndex == currentCallStackNode.Children.Count && currentCallStackNode.Id != CallTree.RootNodeCallStackId)
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

        await Logger.LogResultAsync($"{logMessagePrefix} Total number of leakages: {totalNumberLeakages}");
        await Logger.LogResultAsync($"{logMessagePrefix} Unique leaking instructions: {uniqueLeakingInstructions.Count}");
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

        _callTreeBuilder = new CallTreeBuilder(_mapFileCollection, Logger);
    }

    public override Task UnInitAsync()
    {
        return Task.CompletedTask;
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