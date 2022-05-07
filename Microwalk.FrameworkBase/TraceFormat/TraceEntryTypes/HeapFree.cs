using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
{
    /// <summary>
    /// A memory free.
    /// </summary>
    public class HeapFree : ITraceEntry
    {
        public TraceEntryTypes EntryType => TraceEntryTypes.HeapFree;
        public const int EntrySize = 1 + 4;

        public void FromReader(IFastBinaryReader reader)
        {
            Id = reader.ReadInt32();
        }

        public void Store(IFastBinaryWriter writer)
        {
            writer.WriteByte((byte)TraceEntryTypes.HeapFree);
            writer.WriteInt32(Id);
        }

        /// <summary>
        /// The ID of the freed allocation block.
        /// </summary>
        public int Id { get; set; }
    }
}