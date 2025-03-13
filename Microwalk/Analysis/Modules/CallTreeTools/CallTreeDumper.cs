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
        await using var callTreeDumpWriter = new StreamWriter(File.Create(outputFile));
        
        await DumpNodeRecursivelyAsync(CallTree.Root, 0, callTreeDumpWriter, includeMemoryAccesses);
    }

    /// <summary>
    /// Does a recursive depth-first traversal of the given call tree node and writes its contents to the given stream.
    /// </summary>
    /// <param name="node">Current node.</param>
    /// <param name="level">Current tree level.</param>
    /// <param name="writer"></param>
    /// <param name="includeMemoryAccesses"></param>
    private async Task DumpNodeRecursivelyAsync(SplitNode node, int level, StreamWriter writer, bool includeMemoryAccesses)
    {
        string indentation = new string(' ', 4 * level);

        // There are three types of nodes that have recursive invocations: The tree root, call nodes, and split nodes.
        if(ReferenceEquals(node, CallTree.Root))
        {
            await writer.WriteLineAsync($"{indentation}@root");
        }
        else if(node is CallNode callNode)
        {
            await writer.WriteLineAsync($"{indentation}#call {CallTree.ImageAddresses[callNode.SourceInstructionId].formatted} -> {CallTree.ImageAddresses[callNode.TargetInstructionId].formatted} (${callNode.CallStackId:X16})");
        }
        else // Generic split node
        {
            await writer.WriteLineAsync($"{indentation}@split");

            // We print testcase IDs only for pure split nodes, to improve readability of the output file
            await writer.WriteLineAsync($"{indentation}  Testcases: {Utilities.FormatIntegerSequence(node.TestcaseIds.AsEnumerable())} ({node.TestcaseIds.Count} total)");
        }

        await writer.WriteLineAsync($"{indentation}  Successors:");

        // Process normal successors
        foreach(var successor in node.Successors)
        {
            if(successor is CallNode callSuccessor)
            {
                // Recursively process call node
                await DumpNodeRecursivelyAsync(callSuccessor, level + 1, writer, includeMemoryAccesses);
            }
            else if(successor is ReturnNode returnNode)
            {
                await writer.WriteLineAsync($"{indentation}    #return {CallTree.ImageAddresses[returnNode.SourceInstructionId].formatted} -> {CallTree.ImageAddresses[returnNode.TargetInstructionId].formatted}");
            }
            else if(successor is BranchNode branchNode)
            {
                await writer.WriteLineAsync($"{indentation}    #branch {CallTree.ImageAddresses[branchNode.SourceInstructionId].formatted} -> {(branchNode.Taken ? CallTree.ImageAddresses[branchNode.TargetInstructionId].formatted : "<?> (not taken)")}");
            }
            else if(includeMemoryAccesses && successor is AllocationNode allocationNode)
            {
                if(allocationNode.IsHeap)
                    await writer.WriteLineAsync($"{indentation}    #heapalloc H#{allocationNode.Id}, {allocationNode.Size:x8} bytes");
                else
                    await writer.WriteLineAsync($"{indentation}    #stackalloc S#{allocationNode.Id}, {allocationNode.Size:x8} bytes");
            }
            else if(includeMemoryAccesses && successor is MemoryAccessNode memoryAccessNode)
            {
                await writer.WriteLineAsync($"{indentation}    #memory {CallTree.ImageAddresses[memoryAccessNode.InstructionId].formatted} {(memoryAccessNode.IsWrite ? "writes" : "reads")}");
                if(memoryAccessNode is SimpleMemoryAccessNode simpleMemoryNode)
                {
                    string formattedTargetAddress = (simpleMemoryNode.TargetAddress & CallTree.AddressIdFlagMemory) != 0
                        ? CallTree.MemoryAddresses[simpleMemoryNode.TargetAddress]
                        : CallTree.ImageAddresses[simpleMemoryNode.TargetAddress].formatted;

                    await writer.WriteLineAsync($"{indentation}      {formattedTargetAddress}");
                }
                else if(memoryAccessNode is SplitMemoryAccessNode splitMemoryNode)
                {
                    foreach(var targetAddress in splitMemoryNode.Targets)
                    {
                        string formattedTargetAddress = (targetAddress.Key & CallTree.AddressIdFlagMemory) != 0
                            ? CallTree.MemoryAddresses[targetAddress.Key]
                            : CallTree.ImageAddresses[targetAddress.Key].formatted;

                        await writer.WriteLineAsync($"{indentation}      {formattedTargetAddress} : {Utilities.FormatIntegerSequence(targetAddress.Value.AsEnumerable())} ({targetAddress.Value.Count} total)");
                    }
                }
            }
        }

        // Recursively process split successors
        if(node.SplitSuccessors.Count > 0)
        {
            await writer.WriteLineAsync($"{indentation}  SplitSuccessors:");

            foreach(var splitSuccessor in node.SplitSuccessors) 
                await DumpNodeRecursivelyAsync(splitSuccessor, level + 1, writer, includeMemoryAccesses);
        }
    }
}