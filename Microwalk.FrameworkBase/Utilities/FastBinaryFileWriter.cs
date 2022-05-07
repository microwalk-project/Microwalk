using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Microwalk.FrameworkBase.Utilities;

/// <summary>
/// Provides functions for fast sequential buffered writing into a binary file.
/// This class does only do rudimentary error checking, it mostly relies on the security guarantees by the CLR.
/// </summary>
public class FastBinaryFileWriter : IFastBinaryWriter, IDisposable
{
    private const int _bufferSize = 1 * 1024 * 1024;

    /// <summary>
    /// Current position in the output buffer.
    /// </summary>
    private int _bufferPosition = 0;

    /// <summary>
    /// Output buffer.
    /// </summary>
    private readonly byte[] _buffer = new byte[_bufferSize];

    /// <summary>
    /// Helper for retrieving native pointers to the buffer.
    /// </summary>
    private MemoryHandle _bufferMemoryHandle;

    /// <summary>
    /// Output file stream.
    /// </summary>
    private readonly FileStream _fileStream;

    /// <summary>
    /// Creates a new writer for the given file.
    /// </summary>
    /// <param name="path">Output file path.</param>
    public FastBinaryFileWriter(string path)
    {
        // Open file
        _fileStream = File.Open(path, FileMode.Create, FileAccess.Write);

        // Get native pointer to internal buffer
        _bufferMemoryHandle = _buffer.AsMemory().Pin();
    }

    /// <summary>
    /// Writes the entire buffered data into the output file.
    /// </summary>
    public void Flush()
    {
        if(_bufferPosition == 0)
            return;

        _fileStream.Write(_buffer.AsSpan(0, _bufferPosition));
        _bufferPosition = 0;
    }

    /// <summary>
    /// Ensures that the given number of bytes is available in the internal buffer.
    /// </summary>
    /// <param name="number">Number of bytes.</param>
    /// <returns></returns>
    private void EnsureAvailable(int number)
    {
        // Fast path: The bytes are available already
        if(_bufferPosition + number <= _bufferSize)
            return;

        // Clear buffer
        Flush();

        if(number > _bufferSize)
            throw new Exception("The requested space is bigger than the internal buffer.");
    }

    public void Dispose()
    {
        Flush();
        _fileStream.Dispose();
        _bufferMemoryHandle.Dispose();
    }

    /// <summary>
    /// Writes a byte to the buffer.
    /// </summary>
    public void WriteByte(byte value)
    {
        EnsureAvailable(1);

        // Write and increase position
        _buffer[_bufferPosition++] = value;
    }

    /// <summary>
    /// Writes a boolean to the buffer.
    /// </summary>
    public void WriteBoolean(bool value)
    {
        EnsureAvailable(1);

        // Write and increase position
        _buffer[_bufferPosition++] = value ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Writes a char array to the buffer.
    /// </summary>
    public void WriteChars(char[] value)
    {
        EnsureAvailable(value.Length);

        // Write and increase position
        Encoding.ASCII.GetBytes(value, _buffer.AsSpan(_bufferPosition));
        _bufferPosition += value.Length;
    }

    /// <summary>
    /// Writes a signed 16-bit integer to the buffer.
    /// </summary>
    public unsafe void WriteInt16(short value)
    {
        EnsureAvailable(2);

        // Write and increase position
        Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + _bufferPosition, value);
        _bufferPosition += 2;
    }

    /// <summary>
    /// Writes an unsigned 16-bit integer to the buffer.
    /// </summary>
    public unsafe void WriteUInt16(ushort value)
    {
        EnsureAvailable(2);

        // Write and increase position
        Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + _bufferPosition, value);
        _bufferPosition += 2;
    }

    /// <summary>
    /// Writes a signed 32-bit integer to the buffer.
    /// </summary>
    public unsafe void WriteInt32(int value)
    {
        EnsureAvailable(4);

        // Write and increase position
        Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + _bufferPosition, value);
        _bufferPosition += 4;
    }

    /// <summary>
    /// Writes an unsigned 32-bit integer to the buffer.
    /// </summary>
    public unsafe void WriteUInt32(uint value)
    {
        EnsureAvailable(4);

        // Write and increase position
        Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + _bufferPosition, value);
        _bufferPosition += 4;
    }

    /// <summary>
    /// Writes a signed 64-bit integer to the buffer.
    /// </summary>
    public unsafe void WriteInt64(long value)
    {
        EnsureAvailable(8);

        // Write and increase position
        Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + _bufferPosition, value);
        _bufferPosition += 8;
    }

    /// <summary>
    /// Writes an unsigned 64-bit integer to the buffer.
    /// </summary>
    public unsafe void WriteUInt64(ulong value)
    {
        EnsureAvailable(8);

        // Write and increase position
        Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + _bufferPosition, value);
        _bufferPosition += 8;
    }
}