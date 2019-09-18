using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Microwalk.TraceEntryTypes
{
    /// <summary>
    /// An access to memory allocated on the stack.
    /// </summary>
    public class StackMemoryAccess : TraceEntry
    {
        public override TraceEntryTypes EntryType => TraceEntryTypes.StackMemoryAccess;

        protected override void Init(FastBinaryReader reader)
        {
            IsWrite = reader.ReadBoolean();
            InstructionImageId = reader.ReadInt32();
            InstructionRelativeAddress = reader.ReadUInt32();
            MemoryRelativeAddress = reader.ReadUInt32();
        }

        protected override void Store(BinaryWriter writer)
        {
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
