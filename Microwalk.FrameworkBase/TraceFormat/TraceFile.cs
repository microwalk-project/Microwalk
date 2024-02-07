using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microwalk.FrameworkBase.Exceptions;
using Microwalk.FrameworkBase.TraceFormat.TraceEntryTypes;
using Microwalk.FrameworkBase.Utilities;

namespace Microwalk.FrameworkBase.TraceFormat;

/// <summary>
/// Provides functions to read trace files.
/// Iterating this trace file does not include the trace prefix!
/// </summary>
public class TraceFile : IEnumerable<ITraceEntry>
{
    /// <summary>
    /// The associated trace prefix.
    /// </summary>
    public TracePrefixFile? Prefix { get; }

    /// <summary>
    /// Contains the entry data stored in this trace file.
    /// May be null, if only a path is specified.
    /// </summary>
    protected Memory<byte>? Buffer { get; init; }

    /// <summary>
    /// Path of the trace file, which should be read lazily.
    /// </summary>
    private readonly string? _path;

    /// <summary>
    /// Initializes a new trace file from the given byte buffer, using a previously initialized prefix.
    /// </summary>
    /// <param name="prefix">The previously loaded prefix file.</param>
    /// <param name="buffer">Buffer containing the trace data.</param>
    public TraceFile(TracePrefixFile prefix, Memory<byte> buffer)
        : this()
    {
        Prefix = prefix;
        Buffer = buffer;
    }

    /// <summary>
    /// Creates a trace file instance for the given file path.
    /// Trace data will always be read from the file, instead of loading it into memory.
    /// </summary>
    /// <param name="prefix">The previously loaded prefix file.</param>
    /// <param name="path">Path to the trace file.</param>
    public TraceFile(TracePrefixFile prefix, string path)
        : this()
    {
        Prefix = prefix;
        _path = path;
    }

    /// <summary>
    /// Initializes a new empty trace file. Intended for derived types.
    /// </summary>
    protected TraceFile()
    {
    }

    /// <summary>
    /// Returns all trace entries, including the trace prefix.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<ITraceEntry> GetEntriesWithPrefix() => Prefix == null ? this : Prefix.Concat(this);

    public IEnumerator<ITraceEntry> GetEnumerator()
    {
        if(Buffer == null)
            return new TraceFileEnumerator(new FastBinaryFileReader(_path!));
        else
            return new TraceFileEnumerator(new FastBinaryBufferReader(Buffer.Value));
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        if(Buffer == null)
            return new TraceFileEnumerator(new FastBinaryFileReader(_path!));
        else
            return new TraceFileEnumerator(new FastBinaryBufferReader(Buffer.Value));
    }

    public IEnumerator<ITraceEntry> GetNonAllocatingEnumerator()
    {
        if(Buffer == null)
            return new NonAllocatingTraceFileEnumerator(new FastBinaryFileReader(_path!));
        else
            return new NonAllocatingTraceFileEnumerator(new FastBinaryBufferReader(Buffer.Value));
    }

    public IEnumerator<ITraceEntry> GetNonAllocatingEnumeratorWithPrefix()
    {
        if(Prefix == null)
            return GetNonAllocatingEnumerator();

        if(Buffer == null)
        {
            return new ConcatEnumerator<ITraceEntry>
            (
                new NonAllocatingTraceFileEnumerator(new FastBinaryBufferReader(Prefix.Buffer!.Value)),
                new NonAllocatingTraceFileEnumerator(new FastBinaryFileReader(_path!))
            );
        }
        else
        {
            return new ConcatEnumerator<ITraceEntry>
            (
                new NonAllocatingTraceFileEnumerator(new FastBinaryBufferReader(Prefix.Buffer!.Value)),
                new NonAllocatingTraceFileEnumerator(new FastBinaryBufferReader(Buffer.Value))
            );
        }
    }
}

/// <summary>
/// Enumerator for the <see cref="TraceFile"/> class.
/// </summary>
public class TraceFileEnumerator : IEnumerator<ITraceEntry>
{
    private readonly IFastBinaryReader _reader;

    private ITraceEntry? _current;

    public ITraceEntry Current => _current ?? throw new InvalidOperationException("Current should not be used in this state");
    object IEnumerator.Current => Current;

    public TraceFileEnumerator(IFastBinaryReader reader)
    {
        _reader = reader;
        Reset();
    }

    public bool MoveNext()
    {
        // Done?
        if(_reader.Position >= _reader.Length)
            return false;

        // Read type of next trace entry
        var entryType = (TraceEntryTypes.TraceEntryTypes)_reader.ReadByte();

        // Deserialize trace entry
        _current = entryType switch
        {
            TraceEntryTypes.TraceEntryTypes.HeapAllocation => new HeapAllocation(),
            TraceEntryTypes.TraceEntryTypes.HeapFree => new HeapFree(),
            TraceEntryTypes.TraceEntryTypes.StackAllocation => new StackAllocation(),
            TraceEntryTypes.TraceEntryTypes.Branch => new Branch(),
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

/// <summary>
/// Enumerator for the <see cref="TraceFile"/> class.
/// This enumerator does not allocate memory for the individual trace entries; thus, they must be processed on-the-floy and not stored by the consumer.
/// </summary>
public class NonAllocatingTraceFileEnumerator : IEnumerator<ITraceEntry>
{
    private readonly IFastBinaryReader _reader;

    private ITraceEntry? _current;

    // Preallocated trace entry objects.
    private readonly HeapAllocation _traceEntryHeapAllocation = new();
    private readonly HeapFree _traceEntryHeapFree = new();
    private readonly StackAllocation _traceEntryStackAllocation = new();
    private readonly Branch _traceEntryBranch = new();
    private readonly HeapMemoryAccess _traceEntryHeapMemoryAccess = new();
    private readonly ImageMemoryAccess _traceEntryImageMemoryAccess = new();
    private readonly StackMemoryAccess _traceEntryStackMemoryAccess = new();

    public ITraceEntry Current => _current ?? throw new InvalidOperationException("Current should not be used in this state");
    object IEnumerator.Current => Current;

    public NonAllocatingTraceFileEnumerator(IFastBinaryReader reader)
    {
        _reader = reader;
        Reset();
    }

    public bool MoveNext()
    {
        // Done?
        if(_reader.Position >= _reader.Length)
            return false;

        // Read type of next trace entry
        var entryType = (TraceEntryTypes.TraceEntryTypes)_reader.ReadByte();

        // Deserialize trace entry
        _current = entryType switch
        {
            TraceEntryTypes.TraceEntryTypes.HeapAllocation => _traceEntryHeapAllocation,
            TraceEntryTypes.TraceEntryTypes.HeapFree => _traceEntryHeapFree,
            TraceEntryTypes.TraceEntryTypes.StackAllocation => _traceEntryStackAllocation,
            TraceEntryTypes.TraceEntryTypes.Branch => _traceEntryBranch,
            TraceEntryTypes.TraceEntryTypes.HeapMemoryAccess => _traceEntryHeapMemoryAccess,
            TraceEntryTypes.TraceEntryTypes.ImageMemoryAccess => _traceEntryImageMemoryAccess,
            TraceEntryTypes.TraceEntryTypes.StackMemoryAccess => _traceEntryStackMemoryAccess,
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