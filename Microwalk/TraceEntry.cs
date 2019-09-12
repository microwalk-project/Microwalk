using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
        /// <typeparam name="T">Trace entry type.</typeparam>
        /// <param name="reader">Binary reader containing the trace data.</param>
        /// <returns></returns>
        public static TraceEntry DeserializeNextEntry(FastBinaryReader reader)
        {
            // Read depending on type
            TraceEntryTypes entryType = (TraceEntryTypes)reader.ReadByte();
            switch(entryType)
            {
                case TraceEntryTypes.ImageMemoryAccess:
                    return Deserialize<Microwalk.TraceEntryTypes.ImageMemoryAccess>(reader);
                case TraceEntryTypes.HeapMemoryAccess:
                    return Deserialize<Microwalk.TraceEntryTypes.HeapMemoryAccess>(reader);
                case TraceEntryTypes.StackMemoryAccess:
                    return Deserialize<Microwalk.TraceEntryTypes.StackMemoryAccess>(reader);
                case TraceEntryTypes.Allocation:
                    return Deserialize<Microwalk.TraceEntryTypes.Allocation>(reader);
                case TraceEntryTypes.Free:
                    return Deserialize<Microwalk.TraceEntryTypes.Free>(reader);
                case TraceEntryTypes.Branch:
                    return Deserialize<Microwalk.TraceEntryTypes.Branch>(reader);

                default:
                    throw new TraceFormatException($"Unknown trace entry type.");
            }
        }

        /// <summary>
        /// Deserializes a trace entry from the given binary reader (the type byte is assumed have been read already).
        /// </summary>
        /// <typeparam name="T">Trace entry type.</typeparam>
        /// <param name="reader">Binary reader containing the trace data.</param>
        /// <returns></returns>
        private static T Deserialize<T>(FastBinaryReader reader) where T : TraceEntry, new()
        {
            var traceEntry = new T();
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
