using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Microwalk
{
    /// <summary>
    /// Provides functions to read and write trace files, which mainly consist of a list of <see cref="TraceEntry"/> objects and some associated metadata.
    /// </summary>
    public class TraceFile
    {
        /// <summary>
        /// The associated trace prefix.
        /// </summary>
        public virtual TracePrefixFile Prefix { get; private set; }

        /// <summary>
        /// The entries stored in this trace file.
        /// </summary>
        public List<TraceEntry> Entries { get; private set; }

        /// <summary>
        /// The allocations, indexed by their IDs.
        /// </summary>
        public Dictionary<int, TraceEntryTypes.Allocation> Allocations { get; set; }

        /// <summary>
        /// Reads trace data.
        /// </summary>
        /// <param name="prefix">The previously loaded prefix file.</param>
        /// <param name="reader">Binary reader containing the trace data.</param>
        public TraceFile(TracePrefixFile prefix, FastBinaryReader reader)
            : this(reader)
        {
            // Set prefix
            Prefix = prefix;
        }

        /// <summary>
        /// Reads trace data.
        /// </summary>
        /// <param name="reader">Binary reader containing the trace data.</param>
        protected TraceFile(FastBinaryReader reader)
        {
            // Read entries
            int entryCount = reader.ReadInt32();
            List<TraceEntry> entries = new List<TraceEntry>(entryCount);
            for(int i = 0; i < entryCount; ++i)
            {
                var entry = TraceEntry.DeserializeNextEntry(reader);
                if(entry.EntryType == TraceEntry.TraceEntryTypes.Allocation)
                    Allocations.Add(((TraceEntryTypes.Allocation)entry).Id, (TraceEntryTypes.Allocation)entry);
                entries.Add(entry);
            }
            Entries = entries;
        }

        /// <summary>
        /// Creates a new trace file object from the given prefix and trace entries.
        /// </summary>
        /// <param name="prefix">The associated prefix file.</param>
        /// <param name="entries">The trace entries.</param>
        /// <param name="allocations">The allocations, indexed by their IDs.</param>
        internal TraceFile(TracePrefixFile prefix, List<TraceEntry> entries, Dictionary<int, TraceEntryTypes.Allocation> allocations)
        {
            // Store arguments
            Prefix = prefix;
            Entries = entries;
            Allocations = allocations;
        }

        /// <summary>
        /// Saves the trace file into the given stream.
        /// </summary>
        /// <param name="writer">Stream writer.</param>
        public virtual void Save(BinaryWriter writer)
        {
            // Store entries
            writer.Write(Entries.Count);
            foreach(var entry in Entries)
                entry.SerializeEntry(writer);
        }
    }
}
