using System;
using System.Collections.Generic;
using System.IO;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat
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
        /// Loads a trace prefix file from the given byte buffer.
        /// </summary>
        /// <param name="buffer">Buffer containing the trace data.</param>
        /// <param name="allocations">Optional. Allocation lookup table, indexed by IDs.</param>
        public TracePrefixFile(Memory<byte> buffer, Dictionary<int, HeapAllocation>? allocations = null)
            : base(allocations)
        {
            // Read image file information
            var reader = new FastBinaryReader(buffer);
            int imageFileCount = reader.ReadInt32();
            ImageFiles = new Dictionary<int, ImageFileInfo>();
            for(int i = 0; i < imageFileCount; ++i)
            {
                var imageFile = new ImageFileInfo(reader);
                ImageFiles.Add(imageFile.Id, imageFile);
            }

            // Set internal buffer
            Buffer = buffer.Slice(reader.Position);
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
            /// Reads image data.
            /// </summary>
            /// <param name="reader">Binary reader.</param>
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
            /// Saves image file data.
            /// </summary>
            /// <param name="writer">Binary writer.</param>
            public void Store(FastBinaryWriter writer)
            {
                writer.WriteInt32(Id);
                writer.WriteUInt64(StartAddress);
                writer.WriteUInt64(EndAddress);
                writer.WriteInt32(Name.Length);
                writer.WriteChars(Name.ToCharArray());
                writer.WriteBoolean(Interesting);
            }

            /// <summary>
            /// Image file ID.
            /// </summary>
            public int Id { get; init; }

            /// <summary>
            /// The start address of the image file.
            /// </summary>
            public ulong StartAddress { get; init; }

            /// <summary>
            /// The end address of the image file.
            /// </summary>
            public ulong EndAddress { get; init; }

            /// <summary>
            /// The name of the image file.
            /// </summary>
            public string Name { get; init; } = "";

            /// <summary>
            /// Determines whether traces from this image file are interesting and should therefore be kept.
            /// </summary>
            public bool Interesting { get; init; }
        }
    }
}