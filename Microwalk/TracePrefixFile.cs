using System.Collections.Generic;
using System.IO;
using Microwalk.TraceEntryTypes;

namespace Microwalk
{
    /// <summary>
    /// Represents the trace prefix.
    /// </summary>
    public class TracePrefixFile : TraceFile
    {
        /// <summary>
        /// The loaded images, indexed by their IDs.
        /// </summary>
        public Dictionary<int, ImageFileInfo> ImageFiles { get; }

        /// <summary>
        /// Loads a trace prefix file.
        /// </summary>
        /// <param name="reader">Binary reader to read the trace prefix.</param>
        public TracePrefixFile(FastBinaryReader reader)
            : base(reader)
        {
            // Read image file information
            int imageFileCount = reader.ReadInt32();
            ImageFiles = new Dictionary<int, ImageFileInfo>();
            for(int i = 0; i < imageFileCount; ++i)
            {
                var imageFile = new ImageFileInfo(reader);
                ImageFiles.Add(imageFile.Id, imageFile);
            }
        }

        /// <summary>
        /// Creates a new trace prefix file object from the given trace entries and image file data.
        /// </summary>
        /// <param name="entries">The trace entries.</param>
        /// <param name="imageFiles">The loaded images, indexed by their IDs.</param>
        /// <param name="allocations">The allocations, indexed by their IDs.</param>
        internal TracePrefixFile(List<TraceEntry> entries, Dictionary<int, ImageFileInfo> imageFiles, Dictionary<int, Allocation> allocations)
            : base(null, entries, allocations)
        {
            // Store arguments
            ImageFiles = imageFiles;
        }

        /// <summary>
        /// Saves the trace prefix into the given stream.
        /// </summary>
        /// <param name="writer">Stream writer.</param>
        public override void Save(BinaryWriter writer)
        {
            // Write common trace data
            base.Save(writer);

            // Write image file information
            writer.Write(ImageFiles.Count);
            foreach(var imageFile in ImageFiles)
                imageFile.Value.Store(writer);
        }

        /// <summary>
        /// Describes one loaded image file.
        /// </summary>
        public class ImageFileInfo
        {
            /// <summary>
            /// Creates a new empty image file descriptor.
            /// </summary>
            public ImageFileInfo()
            {
            }

            /// <summary>
            /// Reads the image data from the given stream.
            /// </summary>
            /// <param name="reader">Binary stream reader.</param>
            public ImageFileInfo(FastBinaryReader reader)
            {
                Id = reader.ReadInt32();
                StartAddress = reader.ReadUInt64();
                EndAddress = reader.ReadUInt64();
                int nameLength = reader.ReadInt32();
                Name = reader.ReadString(nameLength);
                Interesting = reader.ReadBoolean();
            }

            /// <summary>
            /// Saves the image file data into the given stream.
            /// </summary>
            /// <param name="writer">Stream writer.</param>
            public void Store(BinaryWriter writer)
            {
                writer.Write(Id);
                writer.Write(StartAddress);
                writer.Write(EndAddress);
                writer.Write(Name.Length);
                writer.Write(Name.ToCharArray());
                writer.Write(Interesting);
            }

            /// <summary>
            /// Image file ID.
            /// </summary>
            public int Id { get; set; }

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
    }
}