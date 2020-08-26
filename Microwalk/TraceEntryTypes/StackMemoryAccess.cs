using System.IO;

namespace Microwalk.TraceEntryTypes
{
    /// <summary>
    /// An access to memory allocated on the stack.
    /// </summary>
    public class StackMemoryAccess : ITraceEntry
    {
        public TraceEntryTypes EntryType => TraceEntryTypes.StackMemoryAccess;

        public void FromReader(FastBinaryReader reader)
        {
            IsWrite = reader.ReadBoolean();
            InstructionImageId = reader.ReadInt32();
            InstructionRelativeAddress = reader.ReadUInt32();
            MemoryRelativeAddress = reader.ReadUInt32();
        }

        public void Store(BinaryWriter writer)
        {
            writer.Write((byte)TraceEntryTypes.StackMemoryAccess);
            writer.Write(IsWrite);
            writer.Write(InstructionImageId);
            writer.Write(InstructionRelativeAddress);
            writer.Write(MemoryRelativeAddress);
        }

        /// <summary>
        /// Determines whether this is a write access.
        /// </summary>
        public bool IsWrite { get; set; }

        /// <summary>
        /// The image ID of the accessing instruction.
        /// </summary>
        public int InstructionImageId { get; set; }

        /// <summary>
        /// The address of the accessing instruction, relative to the image start address.
        /// </summary>
        public uint InstructionRelativeAddress { get; set; }

        /// <summary>
        /// The address of the accessed memory, relative to the stack minimum address.
        /// </summary>
        public uint MemoryRelativeAddress { get; set; }
    }
}