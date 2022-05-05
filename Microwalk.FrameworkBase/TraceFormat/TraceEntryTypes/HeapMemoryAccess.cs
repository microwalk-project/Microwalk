using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
{
    /// <summary>
    /// An access to memory allocated on the heap.
    /// </summary>
    public class HeapMemoryAccess : ITraceEntry
    {
        public TraceEntryTypes EntryType => TraceEntryTypes.HeapMemoryAccess;
        public const int EntrySize = 1 + 1 + 2 + 4 + 4 + 4 + 4;

        public void FromReader(IFastBinaryReader reader)
        {
            IsWrite = reader.ReadBoolean();
            Size = reader.ReadInt16();
            InstructionImageId = reader.ReadInt32();
            InstructionRelativeAddress = reader.ReadUInt32();
            HeapAllocationBlockId = reader.ReadInt32();
            MemoryRelativeAddress = reader.ReadUInt32();
        }

        public void Store(FastBinaryWriter writer)
        {
            writer.WriteByte((byte)TraceEntryTypes.HeapMemoryAccess);
            writer.WriteBoolean(IsWrite);
            writer.WriteInt16(Size);
            writer.WriteInt32(InstructionImageId);
            writer.WriteUInt32(InstructionRelativeAddress);
            writer.WriteInt32(HeapAllocationBlockId);
            writer.WriteUInt32(MemoryRelativeAddress);
        }

        /// <summary>
        /// Determines whether this is a write access.
        /// </summary>
        public bool IsWrite { get; set; }

        /// <summary>
        /// Size of the memory access.
        /// </summary>
        public short Size { get; set; }

        /// <summary>
        /// The image ID of the accessing instruction.
        /// </summary>
        public int InstructionImageId { get; set; }

        /// <summary>
        /// The address of the accessing instruction, relative to the image start address.
        /// </summary>
        public uint InstructionRelativeAddress { get; set; }

        /// <summary>
        /// The allocation block ID of the accessed memory.
        /// </summary>
        public int HeapAllocationBlockId { get; set; }

        /// <summary>
        /// The address of the accessed memory, relative to the allocated block's start address.
        /// </summary>
        public uint MemoryRelativeAddress { get; set; }
    }
}