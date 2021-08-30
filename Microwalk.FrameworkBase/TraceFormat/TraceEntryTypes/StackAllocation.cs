using System.IO;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
{
    /// <summary>
    /// A stack allocation.
    /// </summary>
    public struct StackAllocation : ITraceEntry
    {
        public TraceEntryTypes EntryType => TraceEntryTypes.StackAllocation;

        public void FromReader(FastBinaryReader reader)
        {
            Id = reader.ReadInt32();
            InstructionImageId = reader.ReadInt32();
            InstructionRelativeAddress = reader.ReadUInt32();
            Size = reader.ReadUInt32();
            Address = reader.ReadUInt64();
        }

        public void Store(BinaryWriter writer)
        {
            writer.Write((byte)TraceEntryTypes.StackAllocation);
            writer.Write(Id);
            writer.Write(InstructionImageId);
            writer.Write(InstructionRelativeAddress);
            writer.Write(Size);
            writer.Write(Address);
        }

        /// <summary>
        /// The ID of the allocated block.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// The image ID of the instruction which makes the stack allocation.
        /// </summary>
        public int InstructionImageId { get; set; }

        /// <summary>
        /// The address of the allocating instruction, relative to the image start address.
        /// </summary>
        public uint InstructionRelativeAddress { get; set; }

        /// <summary>
        /// The size of the allocated memory.
        /// </summary>
        public uint Size { get; set; }

        /// <summary>
        /// The base address of the allocated memory.
        /// </summary>
        public ulong Address { get; set; }
    }
}