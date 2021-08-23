using System;
using System.Collections;
using System.Collections.Generic;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat
{
    /// <summary>
    /// Provides functions to read trace files.
    /// </summary>
    public class TraceFile : IEnumerable<ITraceEntry>
    {
        /// <summary>
        /// The associated trace prefix.
        /// </summary>
        public TracePrefixFile? Prefix { get; }

        /// <summary>
        /// Contains the entry data stored in this trace file.
        /// </summary>
        protected Memory<byte> Buffer { get; init; }

        /// <summary>
        /// The allocations, indexed by their IDs.
        /// </summary>
        public Dictionary<int, Allocation> Allocations { get; }

        /// <summary>
        /// Initializes a new trace file from the given byte buffer, using a previously initialized prefix.
        /// </summary>
        /// <param name="prefix">The previously loaded prefix file.</param>
        /// <param name="buffer">Buffer containing the trace data.</param>
        /// <param name="allocations">Optional. Allocation lookup table, indexed by IDs.</param>
        public TraceFile(TracePrefixFile prefix, Memory<byte> buffer, Dictionary<int, Allocation>? allocations = null)
            : this(allocations)
        {
            Prefix = prefix;
            Buffer = buffer;
        }

        /// <summary>
        /// Initializes a new empty trace file. Intended for derived types.
        /// </summary>
        /// <param name="allocations">Optional. Allocation lookup table, indexed by IDs.</param>
        protected TraceFile(Dictionary<int, Allocation>? allocations = null)
        {
            Allocations = allocations ?? new Dictionary<int, Allocation>();
        }

        public IEnumerator<ITraceEntry> GetEnumerator() => new TraceFileEnumerator(Buffer);
        IEnumerator IEnumerable.GetEnumerator() => new TraceFileEnumerator(Buffer);
    }

    /// <summary>
    /// Enumerator for the <see cref="TraceFile"/> class.
    /// </summary>
    public class TraceFileEnumerator : IEnumerator<ITraceEntry>
    {
        private readonly FastBinaryReader _reader;

        private ITraceEntry? _current;

        public ITraceEntry Current => _current ?? throw new InvalidOperationException("Current should not be used in this state");
        object IEnumerator.Current => Current;

        public TraceFileEnumerator(Memory<byte> buffer)
        {
            _reader = new FastBinaryReader(buffer);
            Reset();
        }

        public bool MoveNext()
        {
            // Done?
            if(_reader.Position >= _reader.Buffer.Length)
                return false;

            // Read type of next trace entry
            var entryType = (TraceEntryTypes.TraceEntryTypes)_reader.ReadByte();

            // Deserialize trace entry
            _current = entryType switch
            {
                TraceEntryTypes.TraceEntryTypes.Allocation => new Allocation(),
                TraceEntryTypes.TraceEntryTypes.Branch => new Branch(),
                TraceEntryTypes.TraceEntryTypes.Free => new Free(),
                TraceEntryTypes.TraceEntryTypes.HeapMemoryAccess => new HeapMemoryAccess(),
                TraceEntryTypes.TraceEntryTypes.ImageMemoryAccess => new ImageMemoryAccess(),
                TraceEntryTypes.TraceEntryTypes.StackMemoryAccess => new StackMemoryAccess(),
                _ => throw new TraceFormatException("Illegal trace entry type.")
            };
            _current.FromReader(_reader);
            return true;
        }

        public void Reset()
        {
            _reader.Position = 0;
        }

        public void Dispose()
        {
            // Nothing to do here
        }
    }
}