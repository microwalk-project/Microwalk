using System.IO;
using Microwalk.TraceEntryTypes;

namespace Microwalk
{
    /// <summary>
    /// Base class for trace entries.
    /// </summary>
    public abstract class TraceEntry
    {
        /// <summary>
        /// Returns the entry's type.
        /// </summary>
        public abstract TraceEntryTypes EntryType { get; }

        /// <summary>
        /// Initializes the trace entry from the given binary reader.
        /// </summary>
        /// <param name="reader">Binary reader containing the trace data.</param>
        protected abstract void Init(FastBinaryReader reader);

        /// <summary>
        /// Writes the entry data into the given stream.
        /// </summary>
        /// <param name="writer">Stream writer.</param>
        protected abstract void Store(BinaryWriter writer);

        /// <summary>
        /// Writes the entry data into the given stream.
        /// </summary>
        /// <param name="writer">Stream writer.</param>
        public void SerializeEntry(BinaryWriter writer)
        {
            // Write type
            writer.Write((byte)EntryType);

            // Write entry data
            Store(writer);
        }

        /// <summary>
        /// Deserializes an entire trace entry from the given binary reader.
        /// </summary>
        /// <param name="reader">Binary reader containing the trace data.</param>
        /// <returns></returns>
        public static TraceEntry DeserializeNextEntry(FastBinaryReader reader)
        {
            // Read depending on type
            TraceEntryTypes entryType = (TraceEntryTypes)reader.ReadByte();
            return entryType switch
            {
                TraceEntryTypes.ImageMemoryAccess => Deserialize<ImageMemoryAccess>(reader),
                TraceEntryTypes.HeapMemoryAccess => Deserialize<HeapMemoryAccess>(reader),
                TraceEntryTypes.StackMemoryAccess => Deserialize<StackMemoryAccess>(reader),
                TraceEntryTypes.Allocation => Deserialize<Allocation>(reader),
                TraceEntryTypes.Free => Deserialize<Free>(reader),
                TraceEntryTypes.Branch => Deserialize<Branch>(reader),
                _ => throw new TraceFormatException("Unknown trace entry type.")
            };
        }

        /// <summary>
        /// Deserializes a trace entry from the given binary reader (the type byte is assumed have been read already).
        /// </summary>
        /// <typeparam name="TEntry">Trace entry type.</typeparam>
        /// <param name="reader">Binary reader containing the trace data.</param>
        /// <returns></returns>
        private static TEntry Deserialize<TEntry>(FastBinaryReader reader) where TEntry : TraceEntry, new()
        {
            var traceEntry = new TEntry();
            traceEntry.Init(reader);
            return traceEntry;
        }

        /// <summary>
        /// The different types of trace entries.
        /// </summary>
        public enum TraceEntryTypes : byte
        {
            /// <summary>
            /// An access to image file memory (usually .data or .r[o]data sections).
            /// </summary>
            ImageMemoryAccess = 1,

            /// <summary>
            /// An access to memory allocated on the heap.
            /// </summary>
            HeapMemoryAccess = 2,

            /// <summary>
            /// An access to memory allocated on the stack.
            /// </summary>
            StackMemoryAccess = 3,

            /// <summary>
            /// A memory allocation.
            /// </summary>
            Allocation = 4,

            /// <summary>
            /// A memory free.
            /// </summary>
            Free = 5,

            /// <summary>
            /// A code branch.
            /// </summary>
            Branch = 6
        };
    }
}