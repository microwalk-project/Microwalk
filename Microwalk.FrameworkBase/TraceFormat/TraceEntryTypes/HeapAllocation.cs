using System.IO;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
{
    /// <summary>
    /// A memory allocation.
    /// </summary>
    public struct HeapAllocation : ITraceEntry
    {
        public TraceEntryTypes EntryType => TraceEntryTypes.HeapAllocation;

        public void FromReader(FastBinaryReader reader)
        {
            Id = reader.ReadInt32();
            Size = reader.ReadUInt32();
            Address = reader.ReadUInt64();
        }

        public void Store(BinaryWriter writer)
        {
            writer.Write((byte)TraceEntryTypes.HeapAllocation);
            writer.Write(Id);
            writer.Write(Size);
            writer.Write(Address);
        }

        /// <summary>
        /// The ID of the allocated block.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The size of the allocated memory.
        /// </summary>
        public uint Size { get; set; }

        /// <summary>
        /// The address of the allocated memory.
        /// </summary>
        public ulong Address { get; set; }
    }
}