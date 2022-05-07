using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
{
    /// <summary>
    /// A memory allocation.
    /// </summary>
    public class HeapAllocation : ITraceEntry
    {
        public TraceEntryTypes EntryType => TraceEntryTypes.HeapAllocation;
        public const int EntrySize = 1 + 4 + 4 + 8;

        public void FromReader(IFastBinaryReader reader)
        {
            Id = reader.ReadInt32();
            Size = reader.ReadUInt32();
            Address = reader.ReadUInt64();
        }

        public void Store(IFastBinaryWriter writer)
        {
            writer.WriteByte((byte)TraceEntryTypes.HeapAllocation);
            writer.WriteInt32(Id);
            writer.WriteUInt32(Size);
            writer.WriteUInt64(Address);
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