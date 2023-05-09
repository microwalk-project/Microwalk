using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Configuration;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;

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
        /// Determines whether to skip memory accesses.
        /// </summary>
        private bool _skipMemoryAccesses;

        /// <summary>
        /// Determines whether to skip 'jump' entries.
        /// </summary>
        private bool _skipJumps;

        /// <summary>
        /// Determines whether to skip 'return' entries.
        /// </summary>
        private bool _skipReturns;

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
            else
                outputFilePath = Path.Combine(_outputDirectory.FullName, $"t{traceEntity.Id}.trace.preprocessed.txt"); // Guess a name that likely fits

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
            Dictionary<int, HeapAllocation> allocations = new(); // Keep track of heap allocations
            foreach(var entry in entries)
            {
                // Print entry index and proper indentation based on call level
                string entryPrefix = $"[{i,entryIndexMinWidth}] {new string(' ', 2 * callLevel)}";

                // Print entry depending on type
                switch(entry.EntryType)
                {
                    case TraceEntryTypes.HeapAllocation:
                    {
                        // Print entry
                        var allocationEntry = (HeapAllocation)entry;

                        await writer.WriteLineAsync($"{entryPrefix}HeapAlloc: H#{allocationEntry.Id}, {allocationEntry.Address:x16}...{(allocationEntry.Address + allocationEntry.Size):x16}, {allocationEntry.Size} bytes");

                        // Remember allocation
                        allocations.Add(allocationEntry.Id, allocationEntry);

                        break;
                    }

                    case TraceEntryTypes.HeapFree:
                    {
                        // Find matching allocation data
                        var freeEntry = (HeapFree)entry;
                        if(!allocations.TryGetValue(freeEntry.Id, out HeapAllocation? allocationEntry))
                        {
                            await Logger.LogErrorAsync($"Could not find associated allocation block #{freeEntry.Id} for free entry {i}, skipping");
                            await writer.WriteLineAsync($"{entryPrefix}HeapFree: An error occured when formatting this trace entry.");
                        }
                        else
                        {
                            // Print entry
                            await writer.WriteLineAsync($"{entryPrefix}HeapFree: H#{freeEntry.Id}, {allocationEntry.Address:x16}");

                            allocations.Remove(allocationEntry.Id);
                        }

                        break;
                    }

                    case TraceEntryTypes.StackAllocation:
                    {
                        // Print entry
                        var allocationEntry = (StackAllocation)entry;
                        var imageFileInfo = traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[allocationEntry.InstructionImageId];
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(imageFileInfo.Id, imageFileInfo.Name, allocationEntry.InstructionRelativeAddress);

                        await writer.WriteLineAsync($"{entryPrefix}StackAlloc: S#{allocationEntry.Id}, <{formattedInstructionAddress}>, {allocationEntry.Address:x16}...{(allocationEntry.Address + allocationEntry.Size):x16}, {allocationEntry.Size} bytes");

                        break;
                    }

                    case TraceEntryTypes.Branch:
                    {
                        // Retrieve function names of instructions
                        var branchEntry = (Branch)entry;
                        var sourceImageFileInfo = traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[branchEntry.SourceImageId];
                        var destinationImageFileInfo = traceEntity.PreprocessedTraceFile.Prefix.ImageFiles[branchEntry.DestinationImageId];
                        string formattedSource = _mapFileCollection.FormatAddress(sourceImageFileInfo.Id, sourceImageFileInfo.Name, branchEntry.SourceInstructionRelativeAddress);
                        string formattedDestination = branchEntry.Taken
                            ? _mapFileCollection.FormatAddress(destinationImageFileInfo.Id, destinationImageFileInfo.Name, branchEntry.DestinationInstructionRelativeAddress)
                            : "?";

                        // Output entry and update call level
                        if(branchEntry.BranchType == Branch.BranchTypes.Call)
                        {
                            string line = $"{entryPrefix}Call: <{formattedSource}> -> <{formattedDestination}>";
                            await writer.WriteLineAsync(line);
                            callStack.Push(line);
                            ++callLevel;

                            firstReturn = false;
                        }
                        else if(branchEntry.BranchType == Branch.BranchTypes.Return)
                        {
                            if(!_skipReturns)
                                await writer.WriteLineAsync($"{entryPrefix}Return: <{formattedSource}> -> <{formattedDestination}>");

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
                        else if(branchEntry.BranchType == Branch.BranchTypes.Jump && !_skipJumps)
                        {
                            await writer.WriteLineAsync($"{entryPrefix}Jump: <{formattedSource}> -> <{formattedDestination}>, {(branchEntry.Taken ? "" : "not ")}taken");
                        }

                        break;
                    }

                    case TraceEntryTypes.HeapMemoryAccess when !_skipMemoryAccesses:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (HeapMemoryAccess)entry;
                        var imageFileInfo = traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[accessEntry.InstructionImageId];
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(imageFileInfo.Id, imageFileInfo.Name, accessEntry.InstructionRelativeAddress);
                        string formattedAccessType = accessEntry.IsWrite ? "HeapWrite" : "HeapRead";

                        // Find allocation block
                        if(!allocations.TryGetValue(accessEntry.HeapAllocationBlockId, out HeapAllocation? allocationEntry))
                        {
                            await Logger.LogErrorAsync($"Could not find associated allocation block H#{accessEntry.HeapAllocationBlockId} for heap access entry {i}, skipping");
                            await writer.WriteLineAsync($"{entryPrefix}{formattedAccessType}: An error occured when formatting this trace entry.");
                        }
                        else
                        {
                            // Format accessed address
                            string formattedMemoryAddress =
                                $"H#{accessEntry.HeapAllocationBlockId}+{accessEntry.MemoryRelativeAddress:x8} ({(allocationEntry.Address + accessEntry.MemoryRelativeAddress):x16})";

                            // Print entry
                            await writer.WriteLineAsync($"{entryPrefix}{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}], {accessEntry.Size} bytes");
                        }

                        break;
                    }

                    case TraceEntryTypes.StackMemoryAccess when !_skipMemoryAccesses:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (StackMemoryAccess)entry;
                        var imageFileInfo = traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[accessEntry.InstructionImageId];
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(imageFileInfo.Id, imageFileInfo.Name, accessEntry.InstructionRelativeAddress);

                        // Format accessed address
                        string formattedMemoryAddress = $"S#{(accessEntry.StackAllocationBlockId == -1 ? "?" : accessEntry.StackAllocationBlockId)}+{accessEntry.MemoryRelativeAddress:x8}";

                        // Print entry
                        string formattedAccessType = accessEntry.IsWrite ? "StackWrite" : "StackRead";
                        await writer.WriteLineAsync($"{entryPrefix}{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}], {accessEntry.Size} bytes");

                        break;
                    }

                    case TraceEntryTypes.ImageMemoryAccess when !_skipMemoryAccesses:
                    {
                        // Retrieve function name of executed instruction
                        var accessEntry = (ImageMemoryAccess)entry;
                        var instructionImageFileInfo = traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[accessEntry.InstructionImageId];
                        string formattedInstructionAddress = _mapFileCollection.FormatAddress(instructionImageFileInfo.Id, instructionImageFileInfo.Name, accessEntry.InstructionRelativeAddress);

                        // Format accessed address
                        var memoryImageFileInfo = traceEntity.PreprocessedTraceFile.Prefix!.ImageFiles[accessEntry.MemoryImageId];
                        string formattedMemoryAddress = _mapFileCollection.FormatAddress(memoryImageFileInfo.Id, memoryImageFileInfo.Name, accessEntry.MemoryRelativeAddress);

                        // Print entry
                        string formattedAccessType = accessEntry.IsWrite ? "ImageWrite" : "ImageRead";
                        await writer.WriteLineAsync($"{entryPrefix}{formattedAccessType}: <{formattedInstructionAddress}>, [{formattedMemoryAddress}], {accessEntry.Size} bytes");

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

        protected override async Task InitAsync(MappingNode? moduleOptions)
        {
            if(moduleOptions == null)
                throw new ConfigurationException("Missing module configuration.");

            // Output directory
            string outputDirectoryPath = moduleOptions.GetChildNodeOrDefault("output-directory")?.AsString() ?? throw new ConfigurationException("No output directory specified.");
            _outputDirectory = Directory.CreateDirectory(outputDirectoryPath);

            // Optional settings
            _includePrefix = moduleOptions.GetChildNodeOrDefault("include-prefix")?.AsBoolean() ?? false;
            _skipMemoryAccesses = moduleOptions.GetChildNodeOrDefault("skip-memory-accesses")?.AsBoolean() ?? false;
            _skipJumps = moduleOptions.GetChildNodeOrDefault("skip-jumps")?.AsBoolean() ?? false;
            _skipReturns = moduleOptions.GetChildNodeOrDefault("skip-returns")?.AsBoolean() ?? false;

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
        }

        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
        }
    }
}