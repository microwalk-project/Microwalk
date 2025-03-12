using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Microwalk.Analysis.Modules.CallTreeTools;

public class CallTreeDumper
{
    public required CallTree CallTree { get; init; }

    /// <summary>
    /// Writes the call tree to the given file.
    /// </summary>
    /// <param name="outputFile">Output text file.</param>
    /// <param name="includeMemoryAccesses">If true, memory accesses are written into the dump as well (else they are omitted).</param>
    public async Task DumpCallTreeAsync(string outputFile, bool includeMemoryAccesses)
    {
        /*
         * Do a depth-first traversal of the call tree and write it to a text file.
         */
        
        // Write call tree to text file
        await using var callTreeDumpWriter = new StreamWriter(File.Create(outputFile));

        // Iterate call tree
        Stack<(SplitNode Node, int Level, int? SuccessorIndex, int? SplitSuccessorIndex)> nodeStack = new();
        SplitNode currentNode = CallTree.Root;
        int level = 0;
        string indentation = "";
        int? successorIndex = null;
        int? splitSuccessorIndex = null;
        while(true)
        {
            if(successorIndex == null && splitSuccessorIndex == null)
            {
                // First time encounter of this node

                if(ReferenceEquals(currentNode, CallTree.Root))
                {
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}@root");
                }
                else if(currentNode is CallNode callNode)
                {
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}#call {CallTree.ImageAddresses[callNode.SourceInstructionId].formatted} -> {CallTree.ImageAddresses[callNode.TargetInstructionId].formatted} (${callNode.CallStackId:X16})");
                }
                else
                {
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}@split");

                    // Testcase IDs
                    // We print testcase IDs only for pure split nodes, to improve readability of the output file
                    await callTreeDumpWriter.WriteLineAsync($"{indentation}  Testcases: {Utilities.FormatIntegerSequence(currentNode.TestcaseIds.AsEnumerable())} ({currentNode.TestcaseIds.Count} total)");
                }

                await callTreeDumpWriter.WriteLineAsync($"{indentation}  Successors:");

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
                        nodeStack.Push((currentNode, level, i + 1, null));
                        currentNode = callNode;
                        ++level;
                        indentation = new string(' ', 4 * level);
                        successorIndex = null;
                        splitSuccessorIndex = null;

                        goto nextIteration;
                    }

                    if(successorNode is ReturnNode returnNode)
                    {
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}    #return {CallTree.ImageAddresses[returnNode.SourceInstructionId].formatted} -> {CallTree.ImageAddresses[returnNode.TargetInstructionId].formatted}");
                    }
                    else if(successorNode is BranchNode branchNode)
                    {
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}    #branch {CallTree.ImageAddresses[branchNode.SourceInstructionId].formatted} -> {(branchNode.Taken ? CallTree.ImageAddresses[branchNode.TargetInstructionId].formatted : "<?> (not taken)")}");
                    }
                    else if(includeMemoryAccesses && successorNode is AllocationNode allocationNode)
                    {
                        if(allocationNode.IsHeap)
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}    #heapalloc H#{allocationNode.Id}, {allocationNode.Size:x8} bytes");
                        else
                            await callTreeDumpWriter.WriteLineAsync($"{indentation}    #stackalloc S#{allocationNode.Id}, {allocationNode.Size:x8} bytes");
                    }
                    else if(includeMemoryAccesses && successorNode is MemoryAccessNode memoryAccessNode)
                    {
                        await callTreeDumpWriter.WriteLineAsync($"{indentation}    #memory {CallTree.ImageAddresses[memoryAccessNode.InstructionId].formatted} {(memoryAccessNode.IsWrite ? "writes" : "reads")}");
                        if(memoryAccessNode is SimpleMemoryAccessNode simpleMemoryNode)
                        {
                            string formattedTargetAddress = (simpleMemoryNode.TargetAddress & CallTree.AddressIdFlagMemory) != 0
                                ? CallTree.MemoryAddresses[simpleMemoryNode.TargetAddress]
                                : CallTree.ImageAddresses[simpleMemoryNode.TargetAddress].formatted;

                            await callTreeDumpWriter.WriteLineAsync($"{indentation}      {formattedTargetAddress}");
                        }
                        else if(memoryAccessNode is SplitMemoryAccessNode splitMemoryNode)
                        {
                            foreach(var targetAddress in splitMemoryNode.Targets)
                            {
                                string formattedTargetAddress = (targetAddress.Key & CallTree.AddressIdFlagMemory) != 0
                                    ? CallTree.MemoryAddresses[targetAddress.Key]
                                    : CallTree.ImageAddresses[targetAddress.Key].formatted;

                                await callTreeDumpWriter.WriteLineAsync($"{indentation}      {formattedTargetAddress} : {Utilities.FormatIntegerSequence(targetAddress.Value.AsEnumerable())} ({targetAddress.Value.Count} total)");
                            }
                        }
                    }
                }

                // Done, move to split successors
                if(currentNode.SplitSuccessors.Count > 0) // Only print this when there actually _are_ split successors
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