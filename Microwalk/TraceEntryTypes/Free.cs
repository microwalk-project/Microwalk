using System.IO;

namespace Microwalk.TraceEntryTypes
{
    /// <summary>
    /// A memory free.
    /// </summary>
    public class Free : TraceEntry
    {
        public override TraceEntryTypes EntryType => TraceEntryTypes.Free;

        protected override void Init(FastBinaryReader reader)
        {
            Id = reader.ReadInt32();
        }

        protected override void Store(BinaryWriter writer)
        {
            writer.Write(Id);
        }

        /// <summary>
        /// The ID of the freed allocation block.
        /// </summary>
        public int Id { get; set; }
    }
}