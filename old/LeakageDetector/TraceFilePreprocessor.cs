using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LeakageDetector
{
    /// <summary>
    /// This class allows preprocessing of trace files into an easier analyzable (and smaller) format, by removing uninteresting sections and translating addresses to a common offset.
    /// The (usually nonsense) allocation/free suffix of traces is removed also.
    /// </summary>
    internal class TraceFilePreprocessor : IDisposable
    {
        /// <summary>
        /// When this is set to true, the preprocessor only saves the allocation information in the trace prefix, effectively reducing the overhead of all trace files.
        /// </summary>
        private const bool REDUCE_TRACE_PREFIX_SIZE = true;

        /// <summary>
        /// The preprocessed trace prefix.
        /// </summary>
        private MemoryStream _commonTraceFilePrefix = new MemoryStream();

        /// <summary>
        /// The path prefix of the processed traces. This can also contain parts of a filename.
        /// </summary>
        private string _outputFilePathPrefix;

        /// <summary>
        /// The metadata of the different involved images, combined with the respective image ID and sorted by their start address.
        /// </summary>
        private Tuple<int, ImageFileInfo>[] _sortedImages;

        /// <summary>
        /// The metadata of the different involved images, indexed by their ID.
        /// </summary>
        private List<ImageFileInfo> _images;

        /// <summary>
        /// Determines whether the prefix has to be read yet.
        /// </summary>
        private bool _prefixMode = true;

        /// <summary>
        /// The trace entry count of the prefix.
        /// </summary>
        public int PrefixTraceEntryCount { get; private set; }

        /// <summary>
        /// The minimum stack pointer value of the prefix.
        /// </summary>
        private ulong _prefixStackPointerMin = 0xFFFF_FFFF_FFFF_FFFFUL;

        /// <summary>
        /// The maximum stack pointer value of the prefix.
        /// </summary>
        private ulong _prefixStackPointerMax = 0x0000_0000_0000_0000UL;

        /// <summary>
        /// The offset to the reserved space for the trace entry count in the preprocessed prefix.
        /// </summary>
        private int _traceEntryCountOffset = -1;

        /// <summary>
        /// The offset to the reserved space for the minimum stack pointer value in the preprocessed prefix.
        /// </summary>
        private int _stackPointerMinOffset = -1;

        /// <summary>
        /// The offset to the reserved space for the maximum stack pointer value in the preprocessed prefix.
        /// </summary>
        private int _stackPointerMaxOffset = -1;

        /// <summary>
        /// Creates a new preprocessor for the given prefix file and image list.
        /// </summary>
        /// <param name="tracePrefixFilePath">The path of the trace prefix file that all other trace files are based on.</param>
        /// <param name="outputFilePathPrefix">The path prefix of the processed traces. This can also contain parts of a filename.</param>
        /// <param name="images">The metadata of the different involved images.</param>
        public TraceFilePreprocessor(string tracePrefixFilePath, string outputFilePathPrefix, List<ImageFileInfo> images)
        {
            // Remember parameters
            _outputFilePathPrefix = outputFilePathPrefix;
            _images = images;
            _sortedImages = images.Select((image, imageIndex) => new Tuple<int, ImageFileInfo>(imageIndex, image)).OrderBy(img => img.Item2.StartAddress).ToArray();

            // Load prefix file
            PreprocessTraceFile(tracePrefixFilePath, 0);
        }

        /// <summary>
        /// Preprocesses the given trace file.
        /// </summary>
        /// <param name="traceFilePath">The path of the trace file to be preprocessed.</param>
        /// <param name="testcaseId">The testcase ID associated to this trace file.</param>
        public void PreprocessTraceFile(string traceFilePath, int testcaseId)
        {
            // Write prefix if that did not happen yet
            BinaryWriter outputWriter;
            if(_prefixMode)
            {
                _commonTraceFilePrefix = new MemoryStream();
                outputWriter = new BinaryWriter(_commonTraceFilePrefix, Encoding.Default, true);

                // First write image data into the prefix
                outputWriter.Write(_images.Count);
                foreach(var image in _images)
                {
                    outputWriter.Write(image.Name.Length);
                    outputWriter.Write(image.Name.ToCharArray());
                    outputWriter.Write((byte)(image.Interesting ? 1 : 0));
                }

                // Reserve a few bytes for the trace entry count in the prefix
                _traceEntryCountOffset = (int)outputWriter.BaseStream.Position;
                outputWriter.Seek(4, SeekOrigin.Current);

                // Reserve a few bytes for the stack pointer information in the prefix
                _stackPointerMinOffset = (int)outputWriter.BaseStream.Position;
                outputWriter.Seek(8, SeekOrigin.Current);
                _stackPointerMaxOffset = (int)outputWriter.BaseStream.Position;
                outputWriter.Seek(8, SeekOrigin.Current);
            }
            else
            {
                // Open output file and copy prefix
                outputWriter = new BinaryWriter(new MemoryStream((int)_commonTraceFilePrefix.Length), Encoding.Default);
                _commonTraceFilePrefix.Seek(0, SeekOrigin.Begin);
                _commonTraceFilePrefix.CopyTo(outputWriter.BaseStream);
            }

            // Run through trace file entries
            int traceEntryCount = PrefixTraceEntryCount;
            ulong stackPointerMin = _prefixStackPointerMin;
            ulong stackPointerMax = _prefixStackPointerMax;
            ulong lastAllocReturnAddress = 0;
            bool encounteredSizeSinceLastAlloc = false;
            Stack<uint> lastAllocationSizes = new Stack<uint>();
            int i = 0;
            int allocFreeSuffixStartOffset = -1;
            int suffixEntryCount = 0;
            foreach(RawTraceEntry entry in ReadRawTraceFile(traceFilePath))
            {
                ++i;
                /*
                 * - Keep all memory allocation addresses, since they might be passed around between images
                 * - Remove all other entries from image files that are marked as "uninteresting"
                 */

                // Decide depending on entry type
                switch(entry.Type)
                {
                    case RawTraceEntryTypes.AllocSizeParameter:
                    {
                        // Remember size parameter until the address return
                        lastAllocationSizes.Push((uint)entry.Size);
                        encounteredSizeSinceLastAlloc = true;

                        //Program.Log("SIZE " + entry.Size + "\n", Program.LogLevel.Warning);
                        //Debug.WriteLine("SIZE " + entry.Size);

                        break;
                    }

                    case RawTraceEntryTypes.AllocAddressReturn:
                    {
                        // Set suffix offset
                        if(allocFreeSuffixStartOffset == -1)
                            allocFreeSuffixStartOffset = (int)outputWriter.BaseStream.Position;
                        ++suffixEntryCount;

                        // Program.Log("RET " + entry.MemoryAddress, Program.LogLevel.Warning);
                        //Debug.WriteLine("RET " + entry.MemoryAddress.ToString("X16"));
                        if(entry.MemoryAddress == lastAllocReturnAddress && !encounteredSizeSinceLastAlloc)
                        {
                            // Program.Log(" IGNORED\n", Program.LogLevel.Error);
                            break;
                        }
                        //Program.Log(" INCLUDED\n", Program.LogLevel.Success);*/

                        if(lastAllocationSizes.Count == 0)
                        {
                            Program.Log("Error: Encountered allocation address return, but size stack is empty\n", Program.LogLevel.Error);
                            break;
                        }
                        uint size = lastAllocationSizes.Pop();

                        // Write allocation entry
                        // Format: Type [1 byte] | Size [4 bytes] | Raw memory address [8 bytes]
                        outputWriter.Write((byte)TraceEntryTypes.Allocation);
                        outputWriter.Write(size);
                        outputWriter.Write(entry.MemoryAddress);

                        lastAllocReturnAddress = entry.MemoryAddress;
                        encounteredSizeSinceLastAlloc = false;

                        ++traceEntryCount;
                        break;
                    }

                    case RawTraceEntryTypes.FreeAddressParameter:
                    {
                        // Set suffix offset
                        if(allocFreeSuffixStartOffset == -1)
                            allocFreeSuffixStartOffset = (int)outputWriter.BaseStream.Position;
                        ++suffixEntryCount;

                        // Skip nonsense frees
                        if(entry.MemoryAddress == 0)
                            break;

                        // Write free entry
                        // Format: Type [1 byte] | Raw memory address [8 bytes]
                        outputWriter.Write((byte)TraceEntryTypes.Free);
                        outputWriter.Write(entry.MemoryAddress);

                        ++traceEntryCount;
                        break;
                    }

                    case RawTraceEntryTypes.MemoryRead when !_prefixMode || !REDUCE_TRACE_PREFIX_SIZE:
                    {
                        // Reset suffix offset variable
                        allocFreeSuffixStartOffset = -1;
                        suffixEntryCount = 0;

                        // Find image of instruction
                        var (containingImageIndex, containingImage) = FindImage(entry.InstructionAddress);

                        // Interesting?
                        if(containingImageIndex < 0 || !containingImage.Interesting)
                            break;

                        // Detect type of access: Read in image memory or in previously allocated heap memory?
                        var (accessedImageIndex, accessedImage) = FindImage(entry.MemoryAddress);
                        if(accessedImage == null)
                        {
                            // Write entry with relative image offset for instruction
                            // Format: Type [1 byte] | Image index [1 byte] | Relative instruction address [4 bytes] | Raw memory address [8 bytes]
                            outputWriter.Write((byte)TraceEntryTypes.AllocMemoryRead);
                            outputWriter.Write((byte)containingImageIndex);
                            outputWriter.Write((uint)(entry.InstructionAddress - containingImage.StartAddress));
                            outputWriter.Write(entry.MemoryAddress);
                        }
                        else
                        {
                            // Write entry with relative image offset for instruction and memory address
                            // Format: Type [1 byte] | Instruction image index [1 byte] | Relative instruction address [4 bytes] | Memory image index [1 byte] | Relative memory address [4 bytes]
                            outputWriter.Write((byte)TraceEntryTypes.ImageMemoryRead);
                            outputWriter.Write((byte)containingImageIndex);
                            outputWriter.Write((uint)(entry.InstructionAddress - containingImage.StartAddress));
                            outputWriter.Write((byte)accessedImageIndex);
                            outputWriter.Write((uint)(entry.MemoryAddress - accessedImage.StartAddress));
                        }

                        ++traceEntryCount;
                        break;
                    }

                    case RawTraceEntryTypes.MemoryWrite when !_prefixMode || !REDUCE_TRACE_PREFIX_SIZE:
                    {
                        // Reset suffix offset variable
                        allocFreeSuffixStartOffset = -1;
                        suffixEntryCount = 0;

                        // Find image of instruction
                        var (containingImageIndex, containingImage) = FindImage(entry.InstructionAddress);

                        // Interesting?
                        if(containingImageIndex < 0 || !containingImage.Interesting)
                            break;

                        // Detect type of access: Write in image memory or in previously allocated heap memory?
                        var (accessedImageIndex, accessedImage) = FindImage(entry.MemoryAddress);
                        if(accessedImage == null)
                        {
                            // Write entry with relative image offset for instruction
                            // Format: Type [1 byte] | Image index [1 byte] | Relative instruction address [4 bytes] | Raw memory address [8 bytes]
                            outputWriter.Write((byte)TraceEntryTypes.AllocMemoryWrite);
                            outputWriter.Write((byte)containingImageIndex);
                            outputWriter.Write((uint)(entry.InstructionAddress - containingImage.StartAddress));
                            outputWriter.Write(entry.MemoryAddress);
                        }
                        else
                        {
                            // Write entry with relative image offset for instruction and memory address
                            // Format: Type [1 byte] | Instruction image index [1 byte] | Relative instruction address [4 bytes] | Memory image index [1 byte] | Relative memory address [4 bytes]
                            outputWriter.Write((byte)TraceEntryTypes.ImageMemoryWrite);
                            outputWriter.Write((byte)containingImageIndex);
                            outputWriter.Write((uint)(entry.InstructionAddress - containingImage.StartAddress));
                            outputWriter.Write((byte)accessedImageIndex);
                            outputWriter.Write((uint)(entry.MemoryAddress - accessedImage.StartAddress));
                        }

                        ++traceEntryCount;
                        break;
                    }

                    case RawTraceEntryTypes.Branch when !_prefixMode || !REDUCE_TRACE_PREFIX_SIZE:
                    {
                        // Reset suffix offset variable
                        allocFreeSuffixStartOffset = -1;
                        suffixEntryCount = 0;

                        // Find image of source and destination instruction
                        var (sourceInstructionImageIndex, sourceInstructionImage) = FindImage(entry.InstructionAddress);
                        var (destinationInstructionImageIndex, destinationInstructionImage) = FindImage(entry.MemoryAddress);

                        // Interesting?
                        if(sourceInstructionImageIndex < 0 || destinationInstructionImageIndex < 0 || !sourceInstructionImage.Interesting && !destinationInstructionImage.Interesting)
                            break;

                        // Write entry with relative image offsets
                        // Format: Type [1 byte] | Source image index [1 byte] | Relative source instruction address [4 bytes] | Destination image index [1 byte] | Relative destination instruction address [4 bytes] | Taken [1 byte] | BranchType [1 byte]
                        outputWriter.Write((byte)TraceEntryTypes.Branch);
                        outputWriter.Write((byte)sourceInstructionImageIndex);
                        outputWriter.Write((uint)(entry.InstructionAddress - sourceInstructionImage.StartAddress));
                        outputWriter.Write((byte)destinationInstructionImageIndex);
                        outputWriter.Write((uint)(entry.MemoryAddress - destinationInstructionImage.StartAddress));
                        outputWriter.Write((entry.Flag & 1) == 1);
                        outputWriter.Write((byte)(entry.Flag >> 1));

                        ++traceEntryCount;
                        break;
                    }

                    case RawTraceEntryTypes.StackPointerWrite:
                    {
                        // Reset suffix offset variable
                        allocFreeSuffixStartOffset = -1;
                        suffixEntryCount = 0;

                        // Update base stack pointer
                        if(entry.MemoryAddress < stackPointerMin)
                            stackPointerMin = entry.MemoryAddress;
                        if(entry.MemoryAddress > stackPointerMax)
                            stackPointerMax = entry.MemoryAddress;
                        break;
                    }
                }
            }

            // Prefix mode?
            if(_prefixMode)
            {
                // Store counters
                PrefixTraceEntryCount = traceEntryCount;
                _prefixStackPointerMin = stackPointerMin;
                _prefixStackPointerMax = stackPointerMax;
                _prefixMode = false;
            }
            else
            {
                // Write reserved values
                outputWriter.Seek(_traceEntryCountOffset, SeekOrigin.Begin);
                outputWriter.Write(traceEntryCount - suffixEntryCount);
                outputWriter.Seek(_stackPointerMinOffset, SeekOrigin.Begin);
                outputWriter.Write(stackPointerMin);
                outputWriter.Seek(_stackPointerMaxOffset, SeekOrigin.Begin);
                outputWriter.Write(stackPointerMax);

                // Write stream contents into output file
                byte[] outputData = ((MemoryStream)outputWriter.BaseStream).GetBuffer();
                using(FileStream outputFileStream = File.Open($"{_outputFilePathPrefix}{testcaseId}.trace.processed", FileMode.Create, FileAccess.Write))
                    outputFileStream.Write(outputData, 0, (suffixEntryCount > 0 ? allocFreeSuffixStartOffset : outputData.Length));
            }

            // Delete output writer to avoid resource leaks
            outputWriter.Close();
            outputWriter.Dispose();
        }

        /// <summary>
        /// Frees allocated resources.
        /// </summary>
        public void Dispose()
        {
            // Delete prefix memory stream
            _commonTraceFilePrefix.Close();
            _commonTraceFilePrefix.Dispose();
        }

        /// <summary>
        /// Finds the image that contains the given address.
        /// </summary>
        /// <param name="address">The address to be searched.</param>
        /// <returns></returns>
        private (int imageIndex, ImageFileInfo image) FindImage(ulong address)
        {
            // Find by start address
            // The images are sorted by start address, so the first hit should be the right one - else the correct image does not exist
            // Linearity of search does not matter here, since the image count is expected to be rather small
            for(int i = _sortedImages.Length - 1; i >= 0; --i)
            {
                var img = _sortedImages[i];
                if(img.Item2.StartAddress <= address)
                {
                    // Check end address
                    if(img.Item2.EndAddress >= address)
                        return (img.Item1, img.Item2);
                    return (-1, null);
                }
            }
            return (-1, null);
        }

        /// <summary>
        /// Reads the given raw trace file and enumerates its entries.
        /// </summary>
        /// <param name="fileName">The trace file to be read.</param>
        /// <returns></returns>
        private static IEnumerable<RawTraceEntry> ReadRawTraceFile(string fileName)
        {
            // Read entire trace file into memory, since these files should not get too big
            byte[] traceFile = File.ReadAllBytes(fileName);

            // Return each trace entry individually
            var fileLength = traceFile.Length;
            int traceEntrySize = Marshal.SizeOf(typeof(RawTraceEntry));
            for(long pos = 0; pos < fileLength; pos += traceEntrySize)
                yield return BufferToStruct(traceFile, pos);
        }

        /// <summary>
        /// Converts the byte array at the given position into a <see cref="RawTraceEntry"/> object.
        /// </summary>
        /// <param name="buffer">The buffer where the raw trace entry objects are stored.</param>
        /// <param name="index">The index in the buffer where the desired raw trace entry object starts.</param>
        private static RawTraceEntry BufferToStruct(byte[] buffer, long index)
        {
            // Do unsafe conversion
            unsafe
            {
                fixed (byte* buf = &buffer[index])
                    return *(RawTraceEntry*)buf;
            }
        }

        public class ImageFileInfo
        {
            /// <summary>
            /// The start address of the image file.
            /// </summary>
            public ulong StartAddress { get; set; }

            /// <summary>
            /// The end address of the image file.
            /// </summary>
            public ulong EndAddress { get; set; }

            /// <summary>
            /// The name of the image file.
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Determines whether traces from this image file are interesting and should therefore be kept.
            /// </summary>
            public bool Interesting { get; set; }
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
            /// A write to the stack pointer register (primarily add and retn instructions, that impose large changes).
            /// </summary>
            StackPointerWrite = 7
        };

        /// <summary>
        /// One trace entry, as present in the trace files.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
        struct RawTraceEntry
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
            /// The address of the instruction triggering the trace entry creation.
            /// Used with: MemoryRead, MemoryWrite, Branch.
            /// </summary>
            public ulong InstructionAddress;

            /// <summary>
            /// The accessed/passed memory address.
            /// Used with: MemoryRead, MemoryWrite, AllocAddressReturn, FreeAddressParameter, Branch.
            /// </summary>
            public ulong MemoryAddress;

            /// <summary>
            /// The passed size.
            /// Used with: AllocSizeParameter.
            /// </summary>
            public ulong Size;
        }
    }
}
