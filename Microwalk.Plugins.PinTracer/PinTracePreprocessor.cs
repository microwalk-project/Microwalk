using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microwalk.FrameworkBase;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.Extensions;
using Microwalk.FrameworkBase.Stages;
using Microwalk.FrameworkBase.TraceFormat;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using YamlDotNet.RepresentationModel;

namespace Microwalk.Plugins.PinTracer
{
    [FrameworkModule("pin", "Preprocesses traces generated with the Pin tool.")]
    public class PinTracePreprocessor : PreprocessorStage
    {
        /// <summary>
        /// The preprocessed trace output directory.
        /// </summary>
        private DirectoryInfo? _outputDirectory;

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
        /// Protects the first test case variable.
        /// </summary>
        private readonly SemaphoreSlim _firstTestcaseSemaphore = new(1, 1);

        /// <summary>
        /// Trace prefix data.
        /// </summary>
        private TracePrefixFile _tracePrefix = null!;

        /// <summary>
        /// Metadata about loaded images (is also assigned to the prefix file), sorted by image start address.
        /// </summary>
        private TracePrefixFile.ImageFileInfo[] _imageFiles = null!;

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
        private SortedList<ulong, Allocation>? _tracePrefixAllocationLookup;

        /// <summary>
        /// The last allocation ID used by the trace prefix.
        /// </summary>
        private int _tracePrefixLastAllocationId;

        public override bool SupportsParallelism => true;

        public override async Task PreprocessTraceAsync(TraceEntity traceEntity)
        {
            // Input check
            if(traceEntity.RawTraceFilePath == null)
                throw new Exception("Raw trace file path is null. Is the trace stage missing?");

            // First test case?
            await _firstTestcaseSemaphore.WaitAsync();
            try
            {
                if(_firstTestcase)
                {
                    // Paths
                    string rawTraceFileDirectory = Path.GetDirectoryName(traceEntity.RawTraceFilePath) ?? throw new Exception($"Could not determine directory: {traceEntity.RawTraceFilePath}");
                    string prefixDataFilePath = Path.Combine(rawTraceFileDirectory!, "prefix_data.txt"); // Suppress "possible null" warning
                    string tracePrefixFilePath = Path.Combine(rawTraceFileDirectory, "prefix.trace");

                    // Read image data
                    string[] imageDataLines = await File.ReadAllLinesAsync(prefixDataFilePath);
                    int nextImageFileId = 0;
                    List<TracePrefixFile.ImageFileInfo> imageFiles = new();
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
                        imageFiles.Add(imageFile);
                    }

                    _imageFiles = imageFiles.OrderBy(img => img.StartAddress).ToArray();

                    // Prepare writer for serializing trace data
                    Dictionary<int, Allocation> tracePrefixAllocations;
                    await using var tracePrefixFileStream = new MemoryStream();
                    await using(var tracePrefixFileWriter = new BinaryWriter(tracePrefixFileStream, Encoding.ASCII, true))
                    {
                        // Write image files
                        tracePrefixFileWriter.Write(_imageFiles.Length);
                        foreach(var imageFile in _imageFiles)
                            imageFile.Store(tracePrefixFileWriter);

                        // Load and parse trace prefix data
                        PreprocessFile(tracePrefixFilePath, true, tracePrefixFileWriter, out tracePrefixAllocations);
                    }

                    // Create trace prefix object
                    var preprocessedTracePrefixData = tracePrefixFileStream.GetBuffer().AsMemory(0, (int)tracePrefixFileStream.Length);
                    _tracePrefix = new TracePrefixFile(preprocessedTracePrefixData, tracePrefixAllocations);
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
                        string outputPath = Path.Combine(_outputDirectory!.FullName, "prefix.trace.preprocessed");
                        await using var writer = new BinaryWriter(File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None));
                        writer.Write(preprocessedTracePrefixData.Span);
                    }
                }
            }
            finally
            {
                _firstTestcaseSemaphore.Release();
            }

            // Preprocess trace data
            string rawTraceFilePath = traceEntity.RawTraceFilePath;
            Dictionary<int, Allocation> allocations;
            await using var traceFileStream = new MemoryStream();
            await using(var traceFileWriter = new BinaryWriter(traceFileStream, Encoding.ASCII, true))
                PreprocessFile(rawTraceFilePath, false, traceFileWriter, out allocations);

            // Create trace file object
            var preprocessedTraceData = traceFileStream.GetBuffer().AsMemory(0, (int)traceFileStream.Length);
            var preprocessedTraceFile = new TraceFile(_tracePrefix, preprocessedTraceData, allocations);

            // Keep raw trace?
            if(!_keepRawTraces)
            {
                File.Delete(traceEntity.RawTraceFilePath);
                traceEntity.RawTraceFilePath = null;
            }

            // Store to disk?
            if(_storeTraces)
            {
                traceEntity.PreprocessedTraceFilePath = Path.Combine(_outputDirectory!.FullName, Path.GetFileName(rawTraceFilePath) + ".preprocessed");
                await using var writer = new BinaryWriter(File.Open(traceEntity.PreprocessedTraceFilePath, FileMode.Create, FileAccess.Write, FileShare.None));
                writer.Write(preprocessedTraceData.Span);
            }

            // Keep trace data in memory for the analysis stages
            traceEntity.PreprocessedTraceFile = preprocessedTraceFile;
        }

        /// <summary>
        /// Preprocesses the given raw trace file and emits a preprocessed one.
        /// </summary>
        /// <param name="inputFileName">Input file.</param>
        /// <param name="isPrefix">Determines whether the prefix file is handled.</param>
        /// <param name="traceFileWriter">Binary writer for storing the preprocessed trace data.</param>
        /// <param name="allocations">Allocation lookup table, indexed by IDs.</param>
        /// <remarks>
        /// This function as not designed as asynchronous, to allow unsafe operations and stack allocations.
        /// </remarks>
        private unsafe void PreprocessFile(string inputFileName, bool isPrefix, BinaryWriter traceFileWriter, out Dictionary<int, Allocation> allocations)
        {
            // Read entire trace file into memory, since these files should not get too big
            byte[] inputFile = File.ReadAllBytes(inputFileName);
            int inputFileLength = inputFile.Length;
            int rawTraceEntrySize = Marshal.SizeOf(typeof(RawTraceEntry));

            // Parse trace entries
            var lastAllocationSizes = new Stack<uint>();
            ulong lastAllocReturnAddress = 0;
            bool encounteredSizeSinceLastAlloc = false;
            var allocationLookup = new SortedList<ulong, Allocation>();
            var allocationData = new List<Allocation>();
            int nextAllocationId = isPrefix ? 0 : _tracePrefixLastAllocationId + 1;
            fixed(byte* inputFilePtr = inputFile)
            {
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
                                Logger.LogDebugAsync("Skipped double return of allocated address").Wait();
                                break;
                            }

                            // Allocation stack empty?
                            if(lastAllocationSizes.Count == 0)
                            {
                                Logger.LogErrorAsync("Encountered allocation address return, but size stack is empty").Wait();
                                break;
                            }

                            uint size = lastAllocationSizes.Pop();

                            // Create entry
                            var entry = new Allocation
                            {
                                Id = nextAllocationId++,
                                Size = size,
                                Address = rawTraceEntry.Param2
                            };
                            entry.Store(traceFileWriter);

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
                                Logger.LogWarningAsync($"Free of address {rawTraceEntry.Param2:X16} does not correspond to any allocation, skipping").Wait();
                                break;
                            }

                            // Create entry
                            var entry = new Free
                            {
                                Id = allocationEntry.Id
                            };
                            entry.Store(traceFileWriter);

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
                                Logger.LogWarningAsync($"Could not resolve image information of branch {rawTraceEntry.Param1:X16} -> {rawTraceEntry.Param2:X16}, skipping").Wait();
                                break;
                            }

                            // Interesting?
                            if(!sourceImage!.Interesting && !destinationImage!.Interesting)
                                break;

                            // Create entry
                            var flags = (RawTraceBranchEntryFlags)rawTraceEntry.Flag;
                            var entry = new Branch
                            {
                                SourceImageId = sourceImageId,
                                SourceInstructionRelativeAddress = (uint)(rawTraceEntry.Param1 - sourceImage.StartAddress),
                                DestinationImageId = destinationImageId,
                                DestinationInstructionRelativeAddress = (uint)(rawTraceEntry.Param2 - destinationImage!.StartAddress),
                                Taken = (flags & RawTraceBranchEntryFlags.Taken) != 0
                            };
                            var rawType = flags & RawTraceBranchEntryFlags.BranchEntryTypeMask;
                            if(rawType == RawTraceBranchEntryFlags.Jump)
                                entry.BranchType = Branch.BranchTypes.Jump;
                            else if(rawType == RawTraceBranchEntryFlags.Call)
                                entry.BranchType = Branch.BranchTypes.Call;
                            else if(rawType == RawTraceBranchEntryFlags.Return)
                                entry.BranchType = Branch.BranchTypes.Return;
                            else
                            {
                                Logger.LogErrorAsync($"Unspecified instruction type on branch {rawTraceEntry.Param1:X16} -> {rawTraceEntry.Param2:X16}, skipping").Wait();
                                break;
                            }

                            entry.Store(traceFileWriter);

                            break;
                        }

                        case RawTraceEntryTypes.MemoryRead when !isPrefix:
                        case RawTraceEntryTypes.MemoryWrite when !isPrefix:
                        {
                            // Find image of instruction
                            var (instructionImageId, instructionImage) = FindImage(rawTraceEntry.Param1);
                            if(instructionImageId < 0)
                            {
                                Logger.LogWarningAsync($"Could not resolve image information of instruction {rawTraceEntry.Param1:X16}, skipping").Wait();
                                break;
                            }

                            // Interesting?
                            if(!instructionImage!.Interesting)
                                break;

                            // Resolve access location: Image, stack or heap?
                            bool isWrite = rawTraceEntry.Type == RawTraceEntryTypes.MemoryWrite;
                            if(_stackPointerMin <= rawTraceEntry.Param2 && rawTraceEntry.Param2 <= _stackPointerMax)
                            {
                                // Stack
                                var entry = new StackMemoryAccess
                                {
                                    IsWrite = isWrite,
                                    InstructionImageId = instructionImageId,
                                    InstructionRelativeAddress = (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress),
                                    MemoryRelativeAddress = (uint)(rawTraceEntry.Param2 - _stackPointerMin)
                                };
                                entry.Store(traceFileWriter);
                            }
                            else
                            {
                                // Image
                                var (accessedImageId, accessedImage) = FindImage(rawTraceEntry.Param2);
                                if(accessedImageId >= 0)
                                {
                                    var entry = new ImageMemoryAccess
                                    {
                                        IsWrite = isWrite,
                                        InstructionImageId = instructionImageId,
                                        InstructionRelativeAddress = (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress),
                                        MemoryImageId = accessedImageId,
                                        MemoryRelativeAddress = (uint)(rawTraceEntry.Param2 - accessedImage!.StartAddress)
                                    };
                                    entry.Store(traceFileWriter);
                                }
                                else
                                {
                                    // Heap
                                    var (allocationBlockId, allocationBlock) = FindAllocation(allocationLookup, rawTraceEntry.Param2);
                                    if(allocationBlockId < 0 && _tracePrefixAllocationLookup != null)
                                        (allocationBlockId, allocationBlock) = FindAllocation(_tracePrefixAllocationLookup, rawTraceEntry.Param2);
                                    if(allocationBlockId < 0)
                                    {
                                        Logger.LogWarningAsync(
                                                $"Could not resolve target of memory access {rawTraceEntry.Param1:X16} -> [{rawTraceEntry.Param2:X16}] ({(isWrite ? "write" : "read")}), skipping")
                                            .Wait();
                                        break;
                                    }

                                    var entry = new HeapMemoryAccess
                                    {
                                        IsWrite = isWrite,
                                        InstructionImageId = instructionImageId,
                                        InstructionRelativeAddress = (uint)(rawTraceEntry.Param1 - instructionImage.StartAddress),
                                        MemoryAllocationBlockId = allocationBlockId,
                                        MemoryRelativeAddress = (uint)(rawTraceEntry.Param2 - allocationBlock.Address)
                                    };
                                    entry.Store(traceFileWriter);
                                }
                            }

                            break;
                        }
                    }
                }
            }

            // Create trace file object
            allocations = allocationData.ToDictionary(ad => ad.Id);
            if(isPrefix)
            {
                _tracePrefixAllocationLookup = allocationLookup;
                _tracePrefixLastAllocationId = nextAllocationId - 1;
            }
        }

        /// <summary>
        /// Finds the image that contains the given address and returns its ID, or -1 if the image is not found.
        /// </summary>
        /// <param name="address">The address to be searched.</param>
        /// <returns></returns>
        private (int, TracePrefixFile.ImageFileInfo?) FindImage(ulong address)
        {
            // Find by start address
            // The images are sorted by start address, so the first hit should be the right one - else the correct image does not exist
            // Linearity of search does not matter here, since the image count is expected to be rather small
            for(int i = _imageFiles.Length - 1; i >= 0; --i)
            {
                var img = _imageFiles[i];
                if(img.StartAddress <= address)
                {
                    // Check end address
                    if(img.EndAddress >= address)
                        return (img.Id, img);
                    return (-1, null);
                }
            }

            return (-1, null);
        }

        /// <summary>
        /// Finds the allocation block that contains the given address and returns its ID, or -1 if the block is not found.
        /// </summary>
        /// <param name="allocationLookup">List containing all allocations, sorted by start address in ascending order.</param>
        /// <param name="address">The address to be searched.</param>
        /// <returns></returns>
        private (int, Allocation) FindAllocation(SortedList<ulong, Allocation> allocationLookup, ulong address)
        {
            // Use binary search to find allocation block with start address <= address
            var startAddresses = allocationLookup.Keys;
            int left = 0;
            int right = startAddresses.Count - 1;
            int index = -1;
            bool found = false;
            while(left <= right)
            {
                index = left + ((right - left) / 2);
                ulong startAddress = startAddresses[index];
                if(startAddress == address)
                {
                    found = true;
                    break;
                }
                else if(startAddress < address)
                    left = index + 1;
                else
                    right = index - 1;
            }

            if(!found)
                index = left - 1;
            if(index < 0)
                return (-1, default);

            // Check end address
            var block = allocationLookup[startAddresses[index]];
            if(address <= (block.Address + block.Size))
                return (block.Id, block);
            return (-1, default);
        }

        protected override Task InitAsync(YamlMappingNode? moduleOptions)
        {
            // Extract optional configuration values
            string? outputDirectoryPath = moduleOptions.GetChildNodeWithKey("output-directory")?.GetNodeString();
            if(outputDirectoryPath != null)
            {
                _outputDirectory = new DirectoryInfo(outputDirectoryPath);
                if(!_outputDirectory.Exists)
                    _outputDirectory.Create();
            }

            _storeTraces = moduleOptions.GetChildNodeWithKey("store-traces")?.GetNodeBoolean() ?? false;
            if(_storeTraces && outputDirectoryPath == null)
                throw new ConfigurationException("Missing output directory for preprocessed traces.");
            _keepRawTraces = moduleOptions.GetChildNodeWithKey("keep-raw-traces")?.GetNodeBoolean() ?? false;

            return Task.CompletedTask;
        }

        public override Task UnInitAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// One trace entry, as present in the trace files.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        internal readonly ref struct RawTraceEntry
        {
            /// <summary>
            /// The type of this entry.
            /// </summary>
            public readonly RawTraceEntryTypes Type;

            /// <summary>
            /// Flag.
            /// Used with: Branch.
            /// </summary>
            public readonly byte Flag;

            /// <summary>
            /// (Padding for reliable parsing by analysis programs)
            /// </summary>
            private readonly byte _padding1;

            /// <summary>
            /// (Padding for reliable parsing by analysis programs)
            /// </summary>
            private readonly short _padding2;

            /// <summary>
            /// The address of the instruction triggering the trace entry creation, or the size of an allocation.
            /// Used with: MemoryRead, MemoryWrite, Branch, AllocSizeParameter, StackPointerInfo.
            /// </summary>
            public readonly ulong Param1;

            /// <summary>
            /// The accessed/passed memory address.
            /// Used with: MemoryRead, MemoryWrite, AllocAddressReturn, FreeAddressParameter, Branch, StackPointerInfo.
            /// </summary>
            public readonly ulong Param2;
        }

        /// <summary>
        /// The different types of trace entries, as used in the raw trace files.
        /// </summary>
        internal enum RawTraceEntryTypes : uint
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
        internal enum RawTraceBranchEntryFlags : byte
        {
            /// <summary>
            /// Indicates that the branch was taken.
            /// </summary>
            Taken = 1 << 0,

            /// <summary>
            /// Indicates that this branch is implemented as a JMP instruction (includes conditional jumps).
            /// </summary>
            Jump = 1 << 1,

            /// <summary>
            /// Indicates that this branch is implemented as a CALL instruction.
            /// </summary>
            Call = 2 << 1,

            /// <summary>
            /// Indicates that this branch is implemented as a RET instruction.
            /// </summary>
            Return = 3 << 1,
            
            /// <summary>
            /// The mask used to extract the branch entry type (<see cref="Jump"/>, <see cref="Call"/> or <see cref="Return"/>).
            /// </summary>
            BranchEntryTypeMask = 3 << 1
        }
    }
}