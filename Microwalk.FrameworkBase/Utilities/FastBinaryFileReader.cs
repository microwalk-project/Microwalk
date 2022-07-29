using System;
using System.IO;
using System.Text;

namespace Microwalk.FrameworkBase.Utilities;

/// <summary>
/// Provides functions for fast reading from a file with binary data.
/// This class does only do rudimentary error checking, it mostly relies on the security guarantees by the CLR.
/// Optimized for sequential access.
/// </summary>
public class FastBinaryFileReader : IFastBinaryReader, IDisposable
{
    private const int _chunkSize = 1 * 1024 * 1024;

    /// <summary>
    /// Returns or sets the current read position.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Total length of the binary data.
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// File position of the first byte in the chunk buffer.
    /// </summary>
    private int _chunkPosition;

    /// <summary>
    /// Byte buffer holding the current chunk.
    /// </summary>
    private readonly byte[] _chunk = new byte[_chunkSize];

    /// <summary>
    /// File stream.
    /// </summary>
    private readonly FileStream _fileStream;

    /// <summary>
    /// Loads the given file.
    /// </summary>
    /// <param name="filename">The file to be loaded.</param>
    public FastBinaryFileReader(string filename)
    {
        // Open file
        _fileStream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
        Position = 0;
        _chunkPosition = 0;
        Length = (int)_fileStream.Length;

        // Load first chunk
        ReadFullChunkFromStream(_chunk);
    }

    /// <summary>
    /// Reads data from the stream into the given buffer.
    /// Ensures that the entire buffer is filled by repeating the read, if it does return fewer bytes than requested.
    /// </summary>
    /// <param name="chunk">Chunk buffer to fill.</param>
    private void ReadFullChunkFromStream(Span<byte> chunk)
    {
        int n = 0;
        while(n < chunk.Length)
        {
            int r = _fileStream.Read(chunk[n..]);
            if(r == 0)
                return; // No more bytes

            n += r;
        }
    }

    /// <summary>
    /// Ensures that the given number of bytes is available at the current position.
    /// </summary>
    /// <param name="number">Number of bytes that must be available.</param>
    private void EnsureAvailable(int number)
    {
        // We are most likely within the chunk
        if(_chunkPosition <= Position && Position + number <= _chunkPosition + _chunkSize)
            return;

        // Read data, so the new chunk begins at the current position
        int nextChunkPosition = _chunkPosition + _chunkSize;
        if(nextChunkPosition == Position)
        {
            // Sequential next chunk, no leftover

            ReadFullChunkFromStream(_chunk);
        }
        else if(Position < nextChunkPosition && nextChunkPosition < Position + number)
        {
            // Sequential next chunk, but there is some leftover from the current one

            int leftover = nextChunkPosition - Position;
            var chunkSpan = _chunk.AsSpan();

            // Copy leftover bytes to beginning
            _chunk.AsSpan(_chunkSize - leftover).CopyTo(chunkSpan[..leftover]);

            // Read new bytes
            ReadFullChunkFromStream(chunkSpan[leftover..]);
        }
        else
        {
            // Random chunk

            _fileStream.Seek(Position, SeekOrigin.Begin);
            ReadFullChunkFromStream(_chunk);
        }

        _chunkPosition = Position;
    }

    /// <summary>
    /// Reads a byte from the buffer.
    /// </summary>
    /// <returns></returns>
    public byte ReadByte()
    {
        EnsureAvailable(1);

        // Read and increase position
        int chunkPos = (Position++) - _chunkPosition;
        return _chunk[chunkPos];
    }

    /// <summary>
    /// Reads a boolean from the buffer.
    /// </summary>
    /// <returns></returns>
    public bool ReadBoolean()
    {
        EnsureAvailable(1);

        // Read and increase position
        int chunkPos = (Position++) - _chunkPosition;
        return _chunk[chunkPos] != 0;
    }

    /// <summary>
    /// Reads an ANSI string from the buffer.
    /// </summary>
    /// <returns></returns>
    public unsafe string ReadString(int length)
    {
        EnsureAvailable(length);

        // Read and increase position
        int chunkPos = Position - _chunkPosition;
        string str;
        fixed(byte* buf = &_chunk[chunkPos])
            str = new string((sbyte*)buf, 0, length, Encoding.ASCII);
        Position += length;
        return str;
    }

    /// <summary>
    /// Reads a 16-bit integer from the buffer.
    /// </summary>
    /// <returns></returns>
    public unsafe short ReadInt16()
    {
        EnsureAvailable(2);

        // Read and increase position
        int chunkPos = Position - _chunkPosition;
        short val;
        fixed(byte* buf = &_chunk[chunkPos])
            if((chunkPos & 0b1) == 0) // If the alignment is right, direct conversion is possible
                val = *((short*)buf);
            else
                val = (short)((*buf) | (*(buf + 1) << 8)); // Little Endian
        Position += 2;
        return val;
    }

    /// <summary>
    /// Reads a 32-bit integer from the buffer.
    /// </summary>
    /// <returns></returns>
    public unsafe int ReadInt32()
    {
        EnsureAvailable(4);

        // Read and increase position
        int chunkPos = Position - _chunkPosition;
        int val;
        fixed(byte* buf = &_chunk[chunkPos])
            if((chunkPos & 0b11) == 0) // If the alignment is right, direct conversion is possible
                val = *((int*)buf);
            else
                val = (*buf) | (*(buf + 1) << 8) | (*(buf + 2) << 16) | (*(buf + 3) << 24); // Little Endian
        Position += 4;
        return val;
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer from the buffer.
    /// </summary>
    /// <returns></returns>
    public uint ReadUInt32()
    {
        // Read and increase position
        return unchecked((uint)ReadInt32());
    }

    /// <summary>
    /// Reads a 64-bit integer from the buffer.
    /// </summary>
    /// <returns></returns>
    public unsafe long ReadInt64()
    {
        EnsureAvailable(8);

        // Read and increase position
        int chunkPos = Position - _chunkPosition;
        long val;
        fixed(byte* buf = &_chunk[chunkPos])
            if((chunkPos & 0b111) == 0) // If the alignment is right, direct conversion is possible
                val = *((long*)buf);
            else
            {
                // Little Endian
                int i1 = (*buf) | (*(buf + 1) << 8) | (*(buf + 2) << 16) | (*(buf + 3) << 24);
                int i2 = (*(buf + 4)) | (*(buf + 5) << 8) | (*(buf + 6) << 16) | (*(buf + 7) << 24);
                val = (uint)i1 | ((long)i2 << 32);
            }

        Position += 8;
        return val;
    }

    /// <summary>
    /// Reads a 64-bit unsigned integer from the buffer.
    /// </summary>
    /// <returns></returns>
    public ulong ReadUInt64()
    {
        // Read and increase position
        return unchecked((ulong)ReadInt64());
    }

    public void Dispose()
    {
        _fileStream.Dispose();
    }
}