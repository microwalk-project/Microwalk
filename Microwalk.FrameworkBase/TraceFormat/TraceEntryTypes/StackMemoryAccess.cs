using System.IO;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
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
            Size = reader.ReadInt16();
            InstructionImageId = reader.ReadInt32();
            InstructionRelativeAddress = reader.ReadUInt32();
            StackAllocationBlockId = reader.ReadInt32();
            MemoryRelativeAddress = reader.ReadUInt32();
        }

        public void Store(BinaryWriter writer)
        {
            writer.Write((byte)TraceEntryTypes.StackMemoryAccess);
            writer.Write(IsWrite);
            writer.Write(Size);
            writer.Write(InstructionImageId);
            writer.Write(InstructionRelativeAddress);
            writer.Write(StackAllocationBlockId);
            writer.Write(MemoryRelativeAddress);
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
        /// The allocation block ID of the accessed stack memory.
        /// -1 indicates that the allocation block could not be resolved.
        /// </summary>
        public int StackAllocationBlockId { get; set; }

        /// <summary>
        /// The address of the accessed memory, relative to the allocated block's start address.
        /// </summary>
        public uint MemoryRelativeAddress { get; set; }
    }
}