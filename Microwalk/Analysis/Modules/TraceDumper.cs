using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;
using YamlDotNet.RepresentationModel;

namespace Microwalk.Analysis.Modules
{
    [FrameworkModule("dump", "Dumps preprocessed trace files in a human-readable form.")]
    internal class TraceDumper : AnalysisStage
    {
        /// <summary>
        /// The trace dump output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory = null!;

        /// <summary>
        /// Determines whether to include the trace prefix.
        /// </summary>
        private bool _includePrefix;

        /// <summary>
        /// MAP file collection for resolving symbol names.
        /// </summary>
        private MapFileCollection _mapFileCollection = null!;

        public override bool SupportsParallelism => true;

        public override async Task AddTraceAsync(TraceEntity traceEntity)
        {
            // Input check
            if(traceEntity.PreprocessedTraceFile == null)
                throw new Exception("Preprocessed trace is null. Is the preprocessor stage missing?");

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
            IEnumerable<ITraceEntry> entries = _includePrefix
                ? traceEntity.PreprocessedTraceFile.Prefix!.Concat(traceEntity.PreprocessedTraceFile)
                : traceEntity.PreprocessedTraceFile;

            // Run through entries
            Stack<string> callStack = new();
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
                    case TraceEntryTypes.HeapAllocation:
                    {
                        // Print entry
                        var allocationEntry = (HeapAllocation)entry;
                        
                        await writer.WriteLineAsync(
                            $"HeapAlloc: H#{allocationEntry.Id}, {allocationEntry.Address:X16}...{(allocationEntry.Address + allocationEntry.Size):X16}, {allocationEntry.Size} bytes");

                        break;
                    }

                    case TraceEntryTypes.HeapFree:
                    {
                        // Find matching allocation data
                        var freeEntry = (HeapFree)entry;
                        if(!traceEntity.PreprocessedTraceFile.Allocations.TryGetValue(freeEntry.Id, out HeapAllocation allocationEntry)
                           && !traceEntity.PreprocessedTraceFile.Prefix!.Allocations.TryGetValue(freeEntry.Id, out allocationEntry))
                            await Logger.LogWarningAsync($"Could not find associated allocation block #{freeEntry.Id} for free entry {i}, skipping");
                        else
                        {
                            // Print entry
                            await writer.WriteLineAsync($"HeapFree: H#{freeEntry.Id}, {allocationEntry.Address:X16}");
                        }

                        break;
                    }

                    case TraceEntryTypes.StackAllocation:
                    {
                        // Print entry
                        var allocationEntry = (StackAllocation)entry;
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[allocationEntry.InstructionImageId], allocationEntry.InstructionRelativeAddress);

                        await writer.WriteLineAsync(
                            $"StackAlloc: S#{allocationEntry.Id}, <{formattedInstructionAddress}>, {allocationEntry.Address:X16}...{(allocationEntry.Address + allocationEntry.Size):X16}, {allocationEntry.Size} bytes");

                        break;
                    }

                    case TraceEntryTypes.Branch:
                    {
                        // Retrieve function names of instructions
                        var branchEntry = (Branch)entry;
                        string formattedSource = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[branchEntry.SourceImageId], branchEntry.SourceInstructionRelativeAddress);
                        string formattedDestination = branchEntry.Taken
                            ? _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[branchEntry.DestinationImageId], branchEntry.DestinationInstructionRelativeAddress)
                            : "?";

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
                            if(callLevel < 0)
                            {
                                callLevel = 0;

                                // Just output a warning, this was probably caused by trampoline functions and similar constructions
                                // Ignore the very first return statement if it is not preceded by a call: This is a part of the "begin" marker of a trace.
                                // If the prefix is omitted, the preceding "call" is not encountered by this loop.
                                if(!firstReturn)
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

                    case TraceEntryTypes.HeapMemoryAccess:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (HeapMemoryAccess)entry;
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[accessEntry.InstructionImageId], accessEntry.InstructionRelativeAddress);

                        // Find allocation block
                        if(!traceEntity.PreprocessedTraceFile.Allocations.TryGetValue(accessEntry.HeapAllocationBlockId, out HeapAllocation allocationEntry)
                           && !traceEntity.PreprocessedTraceFile.Prefix.Allocations.TryGetValue(accessEntry.HeapAllocationBlockId, out allocationEntry))
                            await Logger.LogWarningAsync($"Could not find associated allocation block H#{accessEntry.HeapAllocationBlockId} for heap access entry {i}, skipping");
                        else
                        {
                            // Format accessed address
                            string formattedMemoryAddress =
                                $"H#{accessEntry.HeapAllocationBlockId}+{accessEntry.MemoryRelativeAddress:X8} ({(allocationEntry.Address + accessEntry.MemoryRelativeAddress):X16})";

                            // Print entry
                            string formattedAccessType = accessEntry.IsWrite ? "HeapWrite" : "HeapRead";
                            await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}], {accessEntry.Size} bytes");
                        }

                        break;
                    }

                    case TraceEntryTypes.StackMemoryAccess:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (StackMemoryAccess)entry;
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[accessEntry.InstructionImageId], accessEntry.InstructionRelativeAddress);

                        // Format accessed address
                        string formattedMemoryAddress = $"S#{(accessEntry.StackAllocationBlockId == -1 ? "?" : accessEntry.StackAllocationBlockId)}+{accessEntry.MemoryRelativeAddress:X8}";

                        // Print entry
                        string formattedAccessType = accessEntry.IsWrite ? "StackWrite" : "StackRead";
                        await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}], {accessEntry.Size} bytes");

                        break;
                    }

                    case TraceEntryTypes.ImageMemoryAccess:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (ImageMemoryAccess)entry;
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[accessEntry.InstructionImageId], accessEntry.InstructionRelativeAddress);

                        // Format accessed address
                        string formattedMemoryAddress = _mapFileCollection.FormatAddress(traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[accessEntry.MemoryImageId], accessEntry.MemoryRelativeAddress);

                        // Print entry
                        string formattedAccessType = accessEntry.IsWrite ? "ImageWrite" : "ImageRead";
                        await writer.WriteLineAsync($"{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}], {accessEntry.Size} bytes");

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

        protected override async Task InitAsync(YamlMappingNode? moduleOptions)
        {
            // Output directory
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString() ?? throw new ConfigurationException("No output directory specified.");
            _outputDirectory = new DirectoryInfo(outputDirectoryPath);
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            // Include prefix
            _includePrefix = moduleOptions.GetChildNodeWithKey("include-prefix")?.GetNodeBoolean() ?? false;

            // Load MAP files
            _mapFileCollection = new MapFileCollection(Logger);
            var mapFilesNode = moduleOptions.GetChildNodeWithKey("map-files");
            if(mapFilesNode is YamlSequenceNode mapFileListNode)
                foreach(var mapFileNode in mapFileListNode.Children)
                    await _mapFileCollection.LoadMapFileAsync(mapFileNode.GetNodeString() ?? throw new ConfigurationException("Invalid node type in map file list."));
        }

        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
        }
    }
}