using System.IO;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
{
    /// <summary>
    /// A memory free.
    /// </summary>
    public class HeapFree : ITraceEntry
    {
        public TraceEntryTypes EntryType => TraceEntryTypes.HeapFree;

        public void FromReader(FastBinaryReader reader)
        {
            Id = reader.ReadInt32();
        }

        public void Store(BinaryWriter writer)
        {
            writer.Write((byte)TraceEntryTypes.HeapFree);
            writer.Write(Id);
        }

        /// <summary>
        /// The ID of the freed allocation block.
        /// </summary>
        public int Id { get; set; }
    }
}