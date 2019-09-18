using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.Analysis.Modules
{
    [FrameworkModule("dump", "Provides functionality to dump trace files in a human-readable form.")]
    class TraceDumper : AnalysisStage
    {
        /// <summary>
        /// The trace dump output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        /// <summary>
        /// Determines whether to include the trace prefix.
        /// </summary>
        private bool _includePrefix;

        public override bool SupportsParallelism => true;

        public override async Task AddTraceAsync(TraceEntity traceEntity)
        {
            // Open output file for writing
            string outputFilePath;
            if(traceEntity.PreprocessedTraceFilePath != null)
                outputFilePath = Path.Combine(_outputDirectory.FullName, Path.GetFileName(traceEntity.PreprocessedTraceFilePath) + ".txt");
            else if(traceEntity.RawTraceFilePath != null)
                outputFilePath = Path.Combine(_outputDirectory.FullName, Path.GetFileName(traceEntity.RawTraceFilePath) + ".txt");
            else
                outputFilePath = Path.Combine(_outputDirectory.FullName, $"dump_{ traceEntity.Id }.txt");
            using(var writer = new StreamWriter(File.Open(outputFilePath, FileMode.Create)))
            {
                // Compose entry sequence
                int entryCount = 0;
                IEnumerable<TraceEntry> entries;
                if(_includePrefix)
                {
                    entries = traceEntity.PreprocessedTraceFile.Prefix.Entries.Concat(traceEntity.PreprocessedTraceFile.Entries);
                    entryCount = traceEntity.PreprocessedTraceFile.Prefix.Entries.Count + traceEntity.PreprocessedTraceFile.Entries.Count;
                }
                else
                {
                    entries = traceEntity.PreprocessedTraceFile.Entries;
                    entryCount = traceEntity.PreprocessedTraceFile.Entries.Count;
                }

                // Run through entries
                Stack<string> callStack = new Stack<string>();
                int callLevel = 0;
                int entryIndexWidth = (int)Math.Ceiling(Math.Log10(entryCount));
                int i = 0;
                foreach(var entry in entries)
                {
                    // Print entry index and proper identation based on call level
                    await writer.WriteAsync($"[{i.ToString().PadLeft(entryIndexWidth, ' ')}] {new string(' ', 2 * callLevel)}");

                    // Print entry depending on type
                    switch(entry.EntryType)
                    {
                        case TraceEntry.TraceEntryTypes.Allocation:
                        {
                            // Print entry
                            var allocationEntry = (TraceEntryTypes.Allocation)entry;
                            await writer.WriteLineAsync($"Alloc: #{allocationEntry.Id}, {allocationEntry.Address.ToString("X16")}...{(allocationEntry.Address + allocationEntry.Size).ToString("X16")}, {allocationEntry.Size} bytes");

                            break;
                        }

                        case TraceEntry.TraceEntryTypes.Free:
                        {
                            // Find matching allocation data
                            var freeEntry = (TraceEntryTypes.Free)entry;
                            if(!traceEntity.PreprocessedTraceFile.Allocations.TryGetValue(freeEntry.Id, out TraceEntryTypes.Allocation allocationEntry)
                                && !traceEntity.PreprocessedTraceFile.Prefix.Allocations.TryGetValue(freeEntry.Id, out allocationEntry))
                                await Logger.LogWarningAsync($"Could not find associated allocation block #{freeEntry.Id} for free entry {i}, skipping");
                            else
                            {
                                // Print entry
                                await writer.WriteLineAsync($"Free: #{freeEntry.Id}, {allocationEntry.Address.ToString("X16")}");
                            }

                            break;
                        }

                        case TraceEntry.TraceEntryTypes.Branch:
                        {
                            // Retrieve function names of instructions
                            var branchEntry = (TraceEntryTypes.Branch)entry;
                            string formattedSource = traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[branchEntry.SourceImageId].Name + ":" + branchEntry.SourceInstructionRelativeAddress.ToString("X8"); // TODO resolve function names
                            string formattedDestination = traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[branchEntry.DestinationImageId].Name + ":" + branchEntry.DestinationInstructionRelativeAddress.ToString("X8"); // TODO resolve function names

                            // Output entry and update call level
                            if(branchEntry.BranchType == TraceEntryTypes.Branch.BranchTypes.Call)
                            {
                                string line = $"Call: <{formattedSource}> -> <{formattedDestination}>";
                                await writer.WriteLineAsync(line);
                                callStack.Push(line);
                                ++callLevel;
                            }
                            else if(branchEntry.BranchType == TraceEntryTypes.Branch.BranchTypes.Return)
                            {
                                await writer.WriteLineAsync($"Return: <{formattedSource}> -> <{formattedDestination}>");
                                if(callStack.Count() > 0)
                                    callStack.Pop();
                                --callLevel;

                                // Check indentation
                                if(callLevel < 0)
                                {
                                    // Just output a warning, this was probably caused by trampoline functions and similar constructions
                                    callLevel = 0;
                                    await Logger.LogWarningAsync($"Encountered return entry {i}, but call stack is empty; indentation might break here.");
                                }
                            }
                            else if(branchEntry.BranchType == TraceEntryTypes.Branch.BranchTypes.Jump)
                            {
                                await writer.WriteLineAsync($"Jump: <{formattedSource}> -> <{formattedDestination}>, {(branchEntry.Taken ? "" : "not ")}taken");
                            }

                            break;
                        }

                        case TraceEntry.TraceEntryTypes.HeapMemoryAccess:
                        {
                            // Retrieve function name of executed instruction
                            var accessEntry = (TraceEntryTypes.HeapMemoryAccess)entry;
                            string formattedInstructionAddress = traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.InstructionImageId].Name + ":" + accessEntry.InstructionRelativeAddress.ToString("X8"); // TODO resolve function names

                            // Find allocation block
                            if(!traceEntity.PreprocessedTraceFile.Allocations.TryGetValue(accessEntry.MemoryAllocationBlockId, out TraceEntryTypes.Allocation allocationEntry)
                            && !traceEntity.PreprocessedTraceFile.Prefix.Allocations.TryGetValue(accessEntry.MemoryAllocationBlockId, out allocationEntry))
                                await Logger.LogWarningAsync($"Could not find associated allocation block #{accessEntry.MemoryAllocationBlockId} for heap access entry {i}, skipping");
                            else
                            {
                                // Format accessed address
                                string formattedMemoryAddress = $"#{accessEntry.MemoryAllocationBlockId}+{accessEntry.MemoryRelativeAddress.ToString("X8")} ({(allocationEntry.Address + accessEntry.MemoryRelativeAddress).ToString("X16")})";

                                // Print entry
                                string formattedAccessType = accessEntry.IsWrite ? "HeapRead" : "HeapWrite";
                                await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}]");
                            }

                            break;
                        }

                        case TraceEntry.TraceEntryTypes.StackMemoryAccess:
                        {
                            // Retrieve function name of executed instruction
                            var accessEntry = (TraceEntryTypes.StackMemoryAccess)entry;
                            string formattedInstructionAddress = traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.InstructionImageId].Name + ":" + accessEntry.InstructionRelativeAddress.ToString("X8"); // TODO resolve function names

                            // Format accessed address
                            string formattedMemoryAddress = $"$+{accessEntry.MemoryRelativeAddress.ToString("X8")}";

                            // Print entry
                            string formattedAccessType = accessEntry.IsWrite ? "StackRead" : "StackWrite";
                            await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}]");

                            break;
                        }

                        case TraceEntry.TraceEntryTypes.ImageMemoryAccess:
                        {
                            // Retrieve function name of executed instruction
                            var accessEntry = (TraceEntryTypes.ImageMemoryAccess)entry;
                            string formattedInstructionAddress = traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.InstructionImageId].Name + ":" + accessEntry.InstructionRelativeAddress.ToString("X8"); // TODO resolve function names

                            // Format accessed address
                            string formattedMemoryAddress = traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.MemoryImageId].Name + ":" + accessEntry.MemoryRelativeAddress.ToString("X8"); // TODO resolve function names

                            // Print entry
                            string formattedAccessType = accessEntry.IsWrite ? "ImageRead" : "ImageWrite";
                            await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}]");

                            break;
                        }
                    }

                    // Next entry
                    ++i;
                }
            }
        }

        public override Task FinishAsync()
        {
            return Task.CompletedTask;
        }

        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Output directory
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString();
            if(outputDirectoryPath == null)
                throw new ConfigurationException("No output directory specified.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            // Include prefix
            _includePrefix = moduleOptions.GetChildNodeWithKey("include-prefix")?.GetNodeBoolean() ?? false;

            // TODO map files


            return Task.CompletedTask;
        }

        public override Task UninitAsync()
        {
            return base.UninitAsync();
        }
    }
}
