using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microwalk.Extensions;
using Microwalk.TraceEntryTypes;
using Microwalk.Utilities;
using YamlDotNet.RepresentationModel;

namespace Microwalk.Analysis.Modules
{
    [FrameworkModule("dump", "Dumps preprocessed trace files in a human-readable form.")]
    internal class TraceDumper : AnalysisStage
    {
        /// <summary>
        /// The trace dump output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        /// <summary>
        /// Determines whether to include the trace prefix.
        /// </summary>
        private bool _includePrefix;

        /// <summary>
        /// MAP file collection for resolving symbol names.
        /// </summary>
        private readonly MapFileCollection _mapFileCollection = new MapFileCollection();

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
                outputFilePath = Path.Combine(_outputDirectory.FullName, $"dump_{traceEntity.Id}.txt");
            await using var writer = new StreamWriter(File.Open(outputFilePath, FileMode.Create));

            // Compose entry sequence
            IEnumerable<ITraceEntry> entries;
            if(_includePrefix)
                entries = traceEntity.PreprocessedTraceFile.Prefix.Concat(traceEntity.PreprocessedTraceFile);
            else
                entries = traceEntity.PreprocessedTraceFile;

            // Run through entries
            Stack<string> callStack = new Stack<string>();
            int callLevel = 0;
            const int entryIndexMinWidth = 5; // Prevent too much misalignment
            int i = 0;
            bool firstReturn = !_includePrefix; // Skip return of "trace begin" marker, if there is no prefix -> suppress false warning
            foreach(var entry in entries)
            {
                // Print entry index and proper indentation based on call level
                await writer.WriteAsync($"[{i,entryIndexMinWidth}] {new string(' ', 2 * callLevel)}");

                // Print entry depending on type
                switch(entry.EntryType)
                {
                    case TraceEntryTypes.TraceEntryTypes.Allocation:
                    {
                        // Print entry
                        var allocationEntry = (Allocation)entry;
                        await writer.WriteLineAsync(
                            $"Alloc: #{allocationEntry.Id}, {allocationEntry.Address:X16}...{(allocationEntry.Address + allocationEntry.Size):X16}, {allocationEntry.Size} bytes");

                        break;
                    }

                    case TraceEntryTypes.TraceEntryTypes.Free:
                    {
                        // Find matching allocation data
                        var freeEntry = (Free)entry;
                        if(!traceEntity.PreprocessedTraceFile.Allocations.TryGetValue(freeEntry.Id, out Allocation allocationEntry)
                           && !traceEntity.PreprocessedTraceFile.Prefix.Allocations.TryGetValue(freeEntry.Id, out allocationEntry))
                            await Logger.LogWarningAsync($"Could not find associated allocation block #{freeEntry.Id} for free entry {i}, skipping");
                        else
                        {
                            // Print entry
                            await writer.WriteLineAsync($"Free: #{freeEntry.Id}, {allocationEntry.Address:X16}");
                        }

                        break;
                    }

                    case TraceEntryTypes.TraceEntryTypes.Branch:
                    {
                        // Retrieve function names of instructions
                        var branchEntry = (Branch)entry;
                        string formattedSource = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[branchEntry.SourceImageId], branchEntry.SourceInstructionRelativeAddress);
                        string formattedDestination = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[branchEntry.DestinationImageId], branchEntry.DestinationInstructionRelativeAddress);

                        // Output entry and update call level
                        if(branchEntry.BranchType == Branch.BranchTypes.Call)
                        {
                            string line = $"Call: <{formattedSource}> -> <{formattedDestination}>";
                            await writer.WriteLineAsync(line);
                            callStack.Push(line);
                            ++callLevel;

                            firstReturn = false;
                        }
                        else if(branchEntry.BranchType == Branch.BranchTypes.Return)
                        {
                            await writer.WriteLineAsync($"Return: <{formattedSource}> -> <{formattedDestination}>");
                            if(callStack.Any())
                                callStack.Pop();
                            --callLevel;

                            // Check indentation
                            // Ignore the very first return statement if it is not preceded by a call: This is a part of the "begin" marker of a trace.
                            // If the prefix is omitted, the preceding "call" is not encountered by this loop.
                            if(callLevel < 0 && !firstReturn)
                            {
                                // Just output a warning, this was probably caused by trampoline functions and similar constructions
                                callLevel = 0;
                                await Logger.LogWarningAsync($"Encountered return entry {i}, but call stack is empty; indentation might break here.");
                            }

                            firstReturn = false;
                        }
                        else if(branchEntry.BranchType == Branch.BranchTypes.Jump)
                        {
                            await writer.WriteLineAsync($"Jump: <{formattedSource}> -> <{formattedDestination}>, {(branchEntry.Taken ? "" : "not ")}taken");
                        }

                        break;
                    }

                    case TraceEntryTypes.TraceEntryTypes.HeapMemoryAccess:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (HeapMemoryAccess)entry;
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.InstructionImageId], accessEntry.InstructionRelativeAddress);

                        // Find allocation block
                        if(!traceEntity.PreprocessedTraceFile.Allocations.TryGetValue(accessEntry.MemoryAllocationBlockId, out Allocation allocationEntry)
                           && !traceEntity.PreprocessedTraceFile.Prefix.Allocations.TryGetValue(accessEntry.MemoryAllocationBlockId, out allocationEntry))
                            await Logger.LogWarningAsync($"Could not find associated allocation block #{accessEntry.MemoryAllocationBlockId} for heap access entry {i}, skipping");
                        else
                        {
                            // Format accessed address
                            string formattedMemoryAddress =
                                $"#{accessEntry.MemoryAllocationBlockId}+{accessEntry.MemoryRelativeAddress:X8} ({(allocationEntry.Address + accessEntry.MemoryRelativeAddress):X16})";

                            // Print entry
                            string formattedAccessType = accessEntry.IsWrite ? "HeapWrite" : "HeapRead";
                            await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}]");
                        }

                        break;
                    }

                    case TraceEntryTypes.TraceEntryTypes.StackMemoryAccess:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (StackMemoryAccess)entry;
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.InstructionImageId], accessEntry.InstructionRelativeAddress);

                        // Format accessed address
                        string formattedMemoryAddress = $"$+{accessEntry.MemoryRelativeAddress:X8}";

                        // Print entry
                        string formattedAccessType = accessEntry.IsWrite ? "StackWrite" : "StackRead";
                        await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}]");

                        break;
                    }

                    case TraceEntryTypes.TraceEntryTypes.ImageMemoryAccess:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (ImageMemoryAccess)entry;
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.InstructionImageId], accessEntry.InstructionRelativeAddress);

                        // Format accessed address
                        string formattedMemoryAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.MemoryImageId], accessEntry.MemoryRelativeAddress);

                        // Print entry
                        string formattedAccessType = accessEntry.IsWrite ? "ImageWrite" : "ImageRead";
                        await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}]");

                        break;
                    }
                }

                // Next entry
                ++i;
            }
        }

        public override Task FinishAsync()
        {
            return Task.CompletedTask;
        }

        internal override async Task InitAsync(YamlMappingNode moduleOptions)
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

            // Load MAP files
            var mapFilesNode = moduleOptions.GetChildNodeWithKey("map-files");
            if(mapFilesNode != null && mapFilesNode is YamlSequenceNode mapFileListNode)
                foreach(var mapFileNode in mapFileListNode.Children)
                    await _mapFileCollection.LoadMapFileAsync(mapFileNode.GetNodeString());
        }
    }
}