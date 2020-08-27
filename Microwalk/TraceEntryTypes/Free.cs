using System.IO;
using Microwalk.Utilities;

namespace Microwalk.TraceEntryTypes
{
    /// <summary>
    /// A memory free.
    /// </summary>
    public class Free : ITraceEntry
    {
        public TraceEntryTypes EntryType => TraceEntryTypes.Free;

        public void FromReader(FastBinaryReader reader)
        {
            Id = reader.ReadInt32();
        }

        public void Store(BinaryWriter writer)
        {
            writer.Write((byte)TraceEntryTypes.Free);
            writer.Write(Id);
        }

        /// <summary>
        /// The ID of the freed allocation block.
        /// </summary>
        public int Id { get; set; }
    }
}