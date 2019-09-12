using Microwalk.TraceGeneration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TracePreprocessing.Modules
{
    [FrameworkModule("pin", "Preprocesses trace generated with the Pin tool.")]
    class PinTracePreprocessor : PreprocessorStage
    {
        /// <summary>
        /// The preprocessed trace output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        /// <summary>
        /// Determines whether preprocessed traces are stored to disk.
        /// </summary>
        private bool _storeTraces;

        /// <summary>
        /// Determines whether raw traces are kept or deleted after preprocessing.
        /// </summary>
        private bool _keepRawTraces;

        /// <summary>
        /// Determines whether the next incoming test case is the first one.
        /// </summary>
        private bool _firstTestcase = true;

        /// <summary>
        /// Trace prefix data.
        /// </summary>
        private TracePrefixFile _tracePrefix = null;

        /// <summary>
        /// Metadata about loaded images (is also assigned to the prefix file), sorted by image start address.
        /// </summary>
        private readonly SortedList<ulong, TracePrefixFile.ImageFileInfo> _imageFiles = new SortedList<ulong, TracePrefixFile.ImageFileInfo>();

        /// <summary>
        /// The minimum stack pointer value (set when reading the prefix).
        /// </summary>
        private ulong _stackPointerMin = 0xFFFF_FFFF_FFFF_FFFFUL;

        /// <summary>
        /// The maximum stack pointer value (set when reading the prefix).
        /// </summary>
        private ulong _stackPointerMax = 0x0000_0000_0000_0000UL;

        /// <summary>
        /// Allocation information from the trace prefix, indexed by start address.
        /// </summary>
        private SortedList<ulong, TraceEntryTypes.Allocation> _tracePrefixAllocationLookup;

        /// <summary>
        /// The last allocation ID used by the trace prefix.
        /// </summary>
        private int _tracePrefixLastAllocationId;

        public override bool SupportsParallelism => true;

        public override async Task PreprocessTraceAsync(TraceEntity traceEntity)
        {
            // First test case?
            if(_firstTestcase)
            {
                // Read image data 
                string prefixDataFilePath = Path.Combine(Path.GetDirectoryName(traceEntity.RawTraceFilePath), "prefix_data.txt");
                string[] imageDataLines = await File.ReadAllLinesAsync(prefixDataFilePath);
                int nextImageFileId = 0;
                foreach(string line in imageDataLines)
                {
                    string[] imageData = line.Split('\t');
                    var imageFile = new TracePrefixFile.ImageFileInfo
                    {
                        Id = nextImageFileId++,
                        Interesting = byte.Parse(imageData[1]) != 0,
                        StartAddress = ulong.Parse(imageData[2], NumberStyles.HexNumber),
                        EndAddress = ulong.Parse(imageData[3], NumberStyles.HexNumber),
                        Name = Path.GetFileName(imageData[4])
                    };
                    _imageFiles.Add(imageFile.StartAddress, imageFile);
                }

                // Handle trace prefix file
                string tracePrefixFilePath = Path.Combine(Path.GetDirectoryName(traceEntity.RawTraceFilePath), "prefix.trace");
                _tracePrefix = (TracePrefixFile)PreprocessFile(tracePrefixFilePath, true);
                _firstTestcase = false;

                // Keep raw trace data?
                if(!_keepRawTraces)
                {
                    File.Delete(prefixDataFilePath);
                    File.Delete(tracePrefixFilePath);
                }

                // Store to disk?
                if(_storeTraces)
                {
                    string outputPath = Path.Combine(_outputDirectory.FullName, "prefix.trace.preprocessed");
                    using(var writer = new BinaryWriter(File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None)))
                        _tracePrefix.Save(writer);
                }
            }

            // Wait for trace prefix to be fully created (might happen in different thread)
            while(_tracePrefix == null)
                await Task.Delay(10);

            // Preprocess trace
            string rawTraceFilePath = traceEntity.RawTraceFilePath;
            var preprocessedTraceFile = PreprocessFile(rawTraceFilePath, false);

            // Keep raw trace?
            if(!_keepRawTraces)
            {
                File.Delete(traceEntity.RawTraceFilePath);
                traceEntity.RawTraceFilePath = null;
            }

            // Store to disk?
            if(_storeTraces)
            {
                traceEntity.PreprocessedTraceFilePath = Path.Combine(_outputDirectory.FullName, Path.GetFileName(rawTraceFilePath) + ".preprocessed");
                using(var writer = new BinaryWriter(File.Open(traceEntity.PreprocessedTraceFilePath, FileMode.Create, FileAccess.Write, FileShare.None)))
                    preprocessedTraceFile.Save(writer);
            }

            // Keep trace data in memory for the analysis stages
            traceEntity.PreprocessedTraceFile = preprocessedTraceFile;
        }

        /// <summary>
        /// Preprocesses the given raw trace file and emits a preprocessed one.
        /// </summary>
        /// <param name="inputFileName">Input file.</param>
        /// <param name="isPrefix">Determines whether the prefix file is handled.</param>
        /// <remarks>
        /// This function as not designed as asynchronous, to allow usage of fast stack allocations (<see cref="Span{T}"/>).
        /// </remarks>
        private unsafe TraceFile PreprocessFile(string inputFileName, bool isPrefix)
        {
            // Read entire trace file into memory, since these files should not get too big
            // TODO does reading chunks provide the same performance?
            byte[] inputFile = File.ReadAllBytes(inputFileName);
            int inputFileLength = inputFile.Length;
            int rawTraceEntrySize = Marshal.SizeOf(typeof(RawTraceEntry));

            // TODO: Suffix handling???

            // Parse trace entries
            var traceEntries = new List<TraceEntry>();
            var lastAllocationSizes = new Stack<uint>();
            ulong lastAllocReturnAddress = 0;
            bool encounteredSizeSinceLastAlloc = false;
            var allocationLookup = new SortedList<ulong, TraceEntryTypes.Allocation>();
            var allocationData = new List<TraceEntryTypes.Allocation>();
            int nextAllocationId = isPrefix ? 0 : _tracePrefixLastAllocationId + 1;
            fixed (byte* inputFilePtr = inputFile)
                for(long pos = 0; pos < inputFileLength; pos += rawTraceEntrySize)
                {
                    // Read entry
                    RawTraceEntry rawTraceEntry = *(RawTraceEntry*)&inputFilePtr[pos];
                    switch(rawTraceEntry.Type)
                    {
                        case RawTraceEntryTypes.AllocSizeParameter:
                        {
                            // Remember size parameter until the address return
                            lastAllocationSizes.Push((uint)rawTraceEntry.Param1);
                            encounteredSizeSinceLastAlloc = true;

                            break;
                        }

                        case RawTraceEntryTypes.AllocAddressReturn:
                        {
                            // Catch double returns of the same allocated address (happens for some allocator implementations)
                            if(rawTraceEntry.Param2 == lastAllocReturnAddress && !encounteredSizeSinceLastAlloc)
                            {
                                Logger.LogWarningAsync("Skipped double return of allocated address").Wait();
                                break;
                            }

                            // Allocation stack empty?
                            if(lastAllocationSizes.Count == 0)
                            {
                                Logger.LogErrorAsync("Encountered allocation address return, but size stack is empty\n").Wait();
                                break;
                            }
                            uint size = lastAllocationSizes.Pop();

                            // Create entry
                            var entry = new TraceEntryTypes.Allocation
                            {
                                Id = nextAllocationId++,
                                Size = size,
                                Address = rawTraceEntry.Param2
                            };
                            traceEntries.Add(entry);

                            // Store allocation information
                            allocationLookup[entry.Address] = entry;
                            allocationData.Add(entry);

                            // Update state
                            lastAllocReturnAddress = entry.Address;
                            encounteredSizeSinceLastAlloc = false;

                            break;
                        }

                        case RawTraceEntryTypes.FreeAddressParameter:
                        {
                            // Skip nonsense frees
                            if(rawTraceEntry.Param2 == 0)
                                break;
                            if(!allocationLookup.TryGetValue(rawTraceEntry.Param2, out var allocationEntry))
                            {
                                Logger.LogWarningAsync($"Free of address {rawTraceEntry.Param2.ToString("X16")} does not correspond to any allocation, skipping").Wait();
                                break;
                            }

                            // Create entry
                            var entry = new TraceEntryTypes.Free
                            {
                                Id = allocationEntry.Id,
                            };
                            traceEntries.Add(entry);

                            // Remove entry from allocation list
                            allocationLookup.Remove(allocationEntry.Address);

                            break;
                        }

                        case RawTraceEntryTypes.StackPointerInfo:
                        {
                            // Save stack pointer data
                            _stackPointerMin = rawTraceEntry.Param1;
                            _stackPointerMax = rawTraceEntry.Param2;

                            break;
                        }

                        case RawTraceEntryTypes.Branch when !isPrefix:
                        {
                            // Find image of source and destination instruction
                            var (sourceImageId, sourceImage) = FindImage(rawTraceEntry.Param1);
                            var (destinationImageId, destinationImage) = FindImage(rawTraceEntry.Param2);
                            if(sourceImageId < 0 || destinationImageId < 0)
                            {
                                Logger.LogWarningAsync($"Could not resolve image information of branch {rawTraceEntry.Param1.ToString("X16")} -> {rawTraceEntry.Param2.ToString("X16")}, skipping").Wait();
                                break;
                            }

                            // Interesting?
                            if(!sourceImage.Interesting && !destinationImage.Interesting)
                                break;

                            // Create entry
                            var flags = (RawTraceBranchEntryFlags)rawTraceEntry.Flag;
                            var entry = new TraceEntryTypes.Branch
                            {
                                SourceImageId = sourceImageId,
                                SourceInstructionRelativeAddress = (uint)(rawTraceEntry.Param1 - sourceImage.StartAddress),
                                DestinationImageId = destinationImageId,
                                DestinationInstructionRelativeAddress = (uint)(rawTraceEntry.Param2 - destinationImage.StartAddress),
                                Taken = flags.HasFlag(RawTraceBranchEntryFlags.Taken)
                            };
                            if(flags.HasFlag(RawTraceBranchEntryFlags.Jump))
                                entry.BranchType = TraceEntryTypes.Branch.BranchTypes.Jump;
                            else if(flags.HasFlag(RawTraceBranchEntryFlags.Call))
                                entry.BranchType = TraceEntryTypes.Branch.BranchTypes.Call;
                            else if(flags.HasFlag(RawTraceBranchEntryFlags.Return))
                                entry.BranchType = TraceEntryTypes.Branch.BranchTypes.Return;
                            else
                            {
                                Logger.LogErrorAsync($"Unspecified instruction type on branch {rawTraceEntry.Param1.ToString("X16")} -> {rawTraceEntry.Param2.ToString("X16")}, skipping").Wait();
                                break;
                            }
                            traceEntries.Add(entry);

                            break;
                        }

                        case RawTraceEntryTypes.MemoryRead when !isPrefix:
                        case RawTraceEntryTypes.MemoryWrite when !isPrefix:
                        {
                            // Find image of instruction
                            var (instructionImageId, instructionImage) = FindImage(rawTraceEntry.Param1);
                            if(instructionImageId < 0)
                            {
                                Logger.LogWarningAsync($"Could not resolve image information of instruction {rawTraceEntry.Param1.ToString("X16")}, skipping").Wait();
                                break;
                            }

                            // Interesting?
                            if(!instructionImage.Interesting)
                                break;

                            // Resolve access location: Image, stack or heap?
                            bool isWrite = rawTraceEntry.Type == RawTraceEntryTypes.MemoryWrite;
                            if(_stackPointerMin <= rawTraceEntry.Param2 && rawTraceEntry.Param2 <= _stackPointerMax)
                            {
                                // Stack
                                var entry = new TraceEntryTypes.StackMemoryAccess
                                {
                                    IsWrite = isWrite,
                                    InstructionImageId = instructionImageId,
                                    InstructionRelativeAddress = (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress),
                                    MemoryRelativeAddress = (uint)(rawTraceEntry.Param2 - _stackPointerMin)
                                };
                                traceEntries.Add(entry);
                            }
                            else
                            {
                                // Image
                                var (accessedImageId, accessedImage) = FindImage(rawTraceEntry.Param2);
                                if(accessedImageId >= 0)
                                {
                                    var entry = new TraceEntryTypes.ImageMemoryAccess
                                    {
                                        IsWrite = isWrite,
                                        InstructionImageId = instructionImageId,
                                        InstructionRelativeAddress = (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress),
                                        MemoryImageId = accessedImageId,
                                        MemoryRelativeAddress = (uint)(rawTraceEntry.Param2 - accessedImage.StartAddress)
                                    };
                                    traceEntries.Add(entry);
                                }
                                else
                                {
                                    // Heap
                                    var (allocationBlockId, allocationBlock) = FindAllocation(allocationLookup, rawTraceEntry.Param2);
                                    if(allocationBlockId < 0)
                                        (allocationBlockId, allocationBlock) = FindAllocation(_tracePrefixAllocationLookup, rawTraceEntry.Param2);
                                    if(allocationBlockId < 0)
                                    {
                                        Logger.LogWarningAsync($"Could not resolve target of memory access {rawTraceEntry.Param1.ToString("X16")} -> [{rawTraceEntry.Param2.ToString("X16")}] ({(isWrite ? "write" : "read")}), skipping").Wait();
                                        break;
                                    }

                                    var entry = new TraceEntryTypes.HeapMemoryAccess
                                    {
                                        IsWrite = isWrite,
                                        InstructionImageId = instructionImageId,
                                        InstructionRelativeAddress = (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress),
                                        MemoryAllocationBlockId = allocationBlockId,
                                        MemoryRelativeAddress = (uint)(rawTraceEntry.Param2 - allocationBlock.Address)
                                    };
                                    traceEntries.Add(entry);
                                }
                            }

                            break;
                        }
                    }
                }

            // Create trace file object
            var allocationDataDictionary = allocationData.ToDictionary(ad => ad.Id);
            if(isPrefix)
            {
                _tracePrefixAllocationLookup = allocationLookup;
                _tracePrefixLastAllocationId = nextAllocationId - 1;
                return new TracePrefixFile(traceEntries, _imageFiles.Select(i => i.Value).ToDictionary(i => i.Id), allocationDataDictionary);
            }
            else
                return new TraceFile(_tracePrefix, traceEntries, allocationDataDictionary);
        }

        /// <summary>
        /// Finds the image that contains the given address and returns its ID, or -1 if the image is not found.
        /// </summary>
        /// <param name="address">The address to be searched.</param>
        /// <returns></returns>
        private (int, TracePrefixFile.ImageFileInfo) FindImage(ulong address)
        {
            // Find by start address
            // The images are sorted by start address, so the first hit should be the right one - else the correct image does not exist
            // Linearity of search does not matter here, since the image count is expected to be rather small
            for(int i = _imageFiles.Count - 1; i >= 0; --i)
            {
                var img = _imageFiles.ElementAt(i);
                if(img.Key <= address)
                {
                    // Check end address
                    if(img.Value.EndAddress >= address)
                        return (img.Value.Id, img.Value);
                    return (-1, null);
                }
            }
            return (-1, null);
        }

        /// <summary>
        /// Finds the allocation block that contains the given address and returns its ID, or -1 if the block is not found.
        /// </summary>
        /// <param name="address">The address to be searched.</param>
        /// <returns></returns>
        private (int, TraceEntryTypes.Allocation) FindAllocation(SortedList<ulong, TraceEntryTypes.Allocation> allocationData, ulong address)
        {
            // Find by start address
            // Reverse search order:
            // - The allocation blocks are sorted by start address, so the first hit should be the right one - else the correct image does not exist
            // - Since free's are not applied to the allocation data list, al
            // TODO: Might become a bottleneck
            for(int i = allocationData.Count - 1; i >= 0; --i)
            {
                var block = allocationData.ElementAt(i);
                if(block.Key <= address)
                {
                    // Check end address
                    if((block.Value.Address + block.Value.Size) >= address)
                        return (block.Value.Id, block.Value);
                    return (-1, null);
                }
            }
            return (-1, null);
        }

        internal override Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Extract optional configuration values
            string outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString();
            if(outputDirectoryPath != null)
            {
                _outputDirectory = new DirectoryInfo(outputDirectoryPath);
                if(!_outputDirectory.Exists)
                    _outputDirectory.Create();
            }
            _storeTraces = moduleOptions.GetChildNodeWithKey("store-traces")?.GetNodeBoolean() ?? false;
            if(_storeTraces && outputDirectoryPath == null)
                throw new ConfigurationException("Missing output directory for preprocessed traces.");

            return Task.CompletedTask;
        }

        public override Task UninitAsync()
        {
            return base.UninitAsync();
        }

        /// <summary>
        /// One trace entry, as present in the trace files.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        ref struct RawTraceEntry
        {
            /// <summary>
            /// The type of this entry.
            /// </summary>
            public RawTraceEntryTypes Type;

            /// <summary>
            /// Flag.
            /// Used with: Branch.
            /// </summary>
            public byte Flag;

            /// <summary>
            /// (Padding for reliable parsing by analysis programs)
            /// </summary>
            private byte _padding1;

            /// <summary>
            /// (Padding for reliable parsing by analysis programs)
            /// </summary>
            private short _padding2;

            /// <summary>
            /// The address of the instruction triggering the trace entry creation, or the size of an allocation.
            /// Used with: MemoryRead, MemoryWrite, Branch, AllocSizeParameter, StackPointerInfo.
            /// </summary>
            public ulong Param1;

            /// <summary>
            /// The accessed/passed memory address.
            /// Used with: MemoryRead, MemoryWrite, AllocAddressReturn, FreeAddressParameter, Branch, StackPointerInfo.
            /// </summary>
            public ulong Param2;
        }

        /// <summary>
        /// The different types of trace entries, as used in the raw trace files.
        /// </summary>
        enum RawTraceEntryTypes : uint
        {
            /// <summary>
            /// A memory read access.
            /// </summary>
            MemoryRead = 1,

            /// <summary>
            /// A memory write access.
            /// </summary>
            MemoryWrite = 2,

            /// <summary>
            /// The size parameter of an allocation (typically malloc).
            /// </summary>
            AllocSizeParameter = 3,

            /// <summary>
            /// The return address of an allocation (typically malloc).
            /// </summary>
            AllocAddressReturn = 4,

            /// <summary>
            /// The address parameter of a deallocation (typically free).
            /// </summary>
            FreeAddressParameter = 5,

            /// <summary>
            /// A code branch.
            /// </summary>
            Branch = 6,

            /// <summary>
            /// Stack pointer information.
            /// </summary>
            StackPointerInfo = 7
        }

        /// <summary>
        /// Flags assigned to a branch entry in the raw trace.
        /// </summary>
        [Flags]
        enum RawTraceBranchEntryFlags : byte
        {
            /// <summary>
            /// Indicates that the branch was taken.
            /// </summary>
            Taken = 1,

            /// <summary>
            /// Indicates that this branch is implemented as a JMP instruction (includes conditional jumps).
            /// </summary>
            Jump = 2,

            /// <summary>
            /// Indicates that this branch is implemented as a CALL instruction.
            /// </summary>
            Call = 4,

            /// <summary>
            /// Indicates that this branch is implemented as a RET instruction.
            /// </summary>
            Return = 8
        }
    }
}
