using System.IO;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes
{
    /// <summary>
    /// Base type for trace entries.
    /// </summary>
    public interface ITraceEntry
    {
        /// <summary>
        /// Returns the entry type.
        /// </summary>
        public TraceEntryTypes EntryType { get; }

        /// <summary>
        /// Initializes the trace entry from the given binary reader.
        /// </summary>
        /// <param name="reader">Binary reader containing the trace data.</param>
        public void FromReader(FastBinaryReader reader);

        /// <summary>
        /// Writes the entry data into the given stream.
        /// </summary>
        /// <param name="writer">Stream writer.</param>
        public void Store(BinaryWriter writer);
    }
}