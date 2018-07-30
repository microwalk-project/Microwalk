using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace LeakageDetector
{
    /// <summary>
    /// Provides functions for reading a trace file.
    /// </summary>
    public class TraceFile
    {
        #region Variables

        /// <summary>
        /// The reader for this trace file.
        /// </summary>
        private ByteArrayReader _traceFileReader = null;

        /// <summary>
        /// The entries contained in this file (for caching).
        /// </summary>
        private TraceEntry[] _entries = null;

        /// <summary>
        /// The amount of entries in this file.
        /// </summary>
        private int _traceEntryCount;

        /// <summary>
        /// The lowest known stack pointer value, that is believed to correspond to the very top of the stack.
        /// </summary>
        private ulong _stackPointerMin;

        /// <summary>
        /// The highest known stack pointer value, that is believed to correspond to the bottom of the stack.
        /// </summary>
        private ulong _stackPointerMax;

        /// <summary>
        /// The offset of the first trace entry, after the header end.
        /// </summary>
        private int _headerEndOffset;

        /// <summary>
        /// The names of the known images.
        /// </summary>
        private IList<string> _knownImages = null;

        /// <summary>
        /// Stores whether an image with a given ID is interesting.
        /// </summary>
        private Dictionary<int, bool> _imagesInteresting = null;

        /// <summary>
        /// Lookup array for the image IDs.
        /// </summary>
        private int[] _imageIdLookup = null;

        /// <summary>
        /// The value ANDed to each memory access address to align it to the desired granularity.
        /// </summary>
        private ulong _alignmentOperand = ~0UL;

        #endregion

        #region Functions

        /// <summary>
        /// Loads the given trace file. It must have been preprocessed using <see cref="TraceFilePreprocessor" />.
        /// </summary>
        /// <param name="filename">The trace file to load.</param>
        /// <param name="knownImages">The names of the known images. This list is extended when new images are encountered.</param>
        /// <param name="granularity">The granularity with which memory addresses should be aligned during trace reading. Must be a power of 2.</param>
        public TraceFile(string filename, IList<string> knownImages, uint granularity)
        {
            // Calculate granularity alignment operand
            // 1 -> 0xFFFFFFFFFFFFFFFF
            // 2 -> 0xFFFFFFFFFFFFFFFE
            // 4 -> 0xFFFFFFFFFFFFFFFC ...
            _alignmentOperand = ~((ulong)granularity - 1);

            // Load trace file
            _traceFileReader = new ByteArrayReader(filename);

            // First read image data
            int imageCount = _traceFileReader.ReadInt32();
            _imageIdLookup = new int[imageCount];
            _imagesInteresting = new Dictionary<int, bool>(imageCount);
            for(int i = 0; i < imageCount; ++i)
            {
                // Get image name and retrieve its ID
                string imageName = _traceFileReader.ReadString(_traceFileReader.ReadInt32());
                if(!knownImages.Contains(imageName))
                    knownImages.Add(imageName);
                int imageId = knownImages.IndexOf(imageName);
                _imageIdLookup[i] = imageId;

                // Remember which images are interesting
                _imagesInteresting.Add(imageId, _traceFileReader.ReadByte() == 1);
            }
            _knownImages = knownImages;

            // Get trace entry count
            _traceEntryCount = _traceFileReader.ReadInt32();

            // Get stack pointer data
            _stackPointerMin = _traceFileReader.ReadUInt64();
            _stackPointerMax = _traceFileReader.ReadUInt64();

            // Save end offset of header
            _headerEndOffset = _traceFileReader.Position;
        }

        /// <summary>
        /// Loads the entire trace file into the internal entry list.
        /// This function might be called multiple times; only the first call actually loads the trace data.
        /// This is MANDATORY if the trace file is to be iterated simultaneously (e.g. by using GetEnumerator() directly).
        /// </summary>
        public void CacheEntries()
        {
            // Store all entries
            if(_entries != null)
                return;
            _entries = ReadEntriesLazily().ToArray();
        }

        /// <summary>
        /// Reads all entries from the trace file using deferred execution.
        /// </summary>
        /// <returns></returns>
        private IEnumerable<TraceEntry> ReadEntriesLazily()
        {
            // Read trace entries
            _traceFileReader.Position = _headerEndOffset;
            for(int i = 0; i < _traceEntryCount; ++i)
            {
                // Read depending on type
                TraceEntryTypes entryType = (TraceEntryTypes)_traceFileReader.ReadByte();
                switch(entryType)
                {
                    case TraceEntryTypes.Allocation:
                    {
                        AllocationEntry entry = new AllocationEntry();
                        entry.Size = _traceFileReader.ReadUInt32();
                        entry.Address = _traceFileReader.ReadUInt64();

                        yield return entry;
                        break;
                    }
                    case TraceEntryTypes.Free:
                    {
                        FreeEntry entry = new FreeEntry();
                        entry.Address = _traceFileReader.ReadUInt64();

                        yield return entry;
                        break;
                    }
                    case TraceEntryTypes.ImageMemoryRead:
                    {
                        ImageMemoryReadEntry entry = new ImageMemoryReadEntry();
                        entry.InstructionImageId = _imageIdLookup[_traceFileReader.ReadByte()];
                        entry.InstructionImageName = _knownImages[entry.InstructionImageId];
                        entry.InstructionAddress = _traceFileReader.ReadUInt32();
                        entry.MemoryImageId = _imageIdLookup[_traceFileReader.ReadByte()];
                        entry.MemoryImageName = _knownImages[entry.MemoryImageId];
                        entry.MemoryAddress =(uint)( _traceFileReader.ReadUInt32() & _alignmentOperand);

                        yield return entry;
                        break;
                    }
                    case TraceEntryTypes.ImageMemoryWrite:
                    {
                        ImageMemoryWriteEntry entry = new ImageMemoryWriteEntry();
                        entry.InstructionImageId = _imageIdLookup[_traceFileReader.ReadByte()];
                        entry.InstructionImageName = _knownImages[entry.InstructionImageId];
                        entry.InstructionAddress = _traceFileReader.ReadUInt32();
                        entry.MemoryImageId = _imageIdLookup[_traceFileReader.ReadByte()];
                        entry.MemoryImageName = _knownImages[entry.MemoryImageId];
                        entry.MemoryAddress = (uint)(_traceFileReader.ReadUInt32() & _alignmentOperand);

                        yield return entry;
                        break;
                    }
                    case TraceEntryTypes.AllocMemoryRead:
                    {
                        AllocMemoryReadEntry entry = new AllocMemoryReadEntry();
                        entry.ImageId = _imageIdLookup[_traceFileReader.ReadByte()];
                        entry.ImageName = _knownImages[entry.ImageId];
                        entry.InstructionAddress = _traceFileReader.ReadUInt32();
                        entry.MemoryAddress = _traceFileReader.ReadUInt64() & _alignmentOperand;

                        yield return entry;
                        break;
                    }
                    case TraceEntryTypes.AllocMemoryWrite:
                    {
                        AllocMemoryWriteEntry entry = new AllocMemoryWriteEntry();
                        entry.ImageId = _imageIdLookup[_traceFileReader.ReadByte()];
                        entry.ImageName = _knownImages[entry.ImageId];
                        entry.InstructionAddress = _traceFileReader.ReadUInt32();
                        entry.MemoryAddress = _traceFileReader.ReadUInt64() & _alignmentOperand;

                        yield return entry;
                        break;
                    }
                    case TraceEntryTypes.Branch:
                    {
                        BranchEntry entry = new BranchEntry();
                        entry.SourceImageId = _imageIdLookup[_traceFileReader.ReadByte()];
                        entry.SourceImageName = _knownImages[entry.SourceImageId];
                        entry.SourceInstructionAddress = _traceFileReader.ReadUInt32();
                        entry.DestinationImageId = _imageIdLookup[_traceFileReader.ReadByte()];
                        entry.DestinationImageName = _knownImages[entry.DestinationImageId];
                        entry.DestinationInstructionAddress = _traceFileReader.ReadUInt32();
                        entry.Taken = _traceFileReader.ReadBoolean();
                        entry.BranchType = (BranchTypes)_traceFileReader.ReadByte();

                        yield return entry;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Returns whether the image with the given ID is considered interesting.
        /// </summary>
        /// <param name="imageId">The ID of the image to be checked.</param>
        /// <returns></returns>
        public bool ImageIsInteresting(int imageId) => _imagesInteresting[imageId];

        #endregion

        #region Properties

        /// <summary>
        /// Returns the trace entries contained in this file.
        /// If this list shall be iterated simultaneously, call <see cref="CacheEntries"/> first.
        /// </summary>
        public IEnumerable<TraceEntry> Entries
        {
            get
            {
                // Entries cached?
                if(_entries != null)
                {
                    // Return cached entries
                    return _entries;
                }
                else
                {
                    // Read from trace file with deferred execution
                    return ReadEntriesLazily();
                }
            }
        }

        /// <summary>
        /// Returns the amount of entries in this trace file.
        /// </summary>
        public int EntryCount => _traceEntryCount;

        /// <summary>
        /// Returns the lowest known stack pointer value, that is believed to correspond to the very top of the stack.
        /// </summary>
        public ulong StackPointerMin => _stackPointerMin;

        /// <summary>
        /// Returns the highest known stack pointer value, that is believed to correspond to the bottom of the stack.
        /// </summary>
        public ulong StackPointerMax => _stackPointerMax;

        #endregion
    }
}
