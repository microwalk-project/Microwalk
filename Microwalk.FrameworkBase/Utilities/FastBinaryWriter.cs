using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Microwalk.FrameworkBase.Utilities
{
    /// <summary>
    /// Provides functions for fast linear writing of binary data.
    /// This class does only do rudimentary error checking, it mostly relies on the security guarantess by the CLR.
    /// </summary>
    public class FastBinaryWriter : IDisposable
    {
        /// <summary>
        /// The current length of the written data.
        /// </summary>
        public int Length { get; private set; }

        /// <summary>
        /// Output buffer.
        /// </summary>
        public byte[] Buffer { get; private set; }

        /// <summary>
        /// Helper for retrieving native pointers to the buffer.
        /// </summary>
        private MemoryHandle _bufferMemoryHandle;

        /// <summary>
        /// Creates a new instance with the given initial capacity.
        /// </summary>
        /// <param name="initialCapacity">
        /// The initial buffer capacity.
        /// Giving a good upper bound of the needed capacity will lead to considerable performance improvements, as no re-allocations are necessary.
        /// </param>
        public FastBinaryWriter(int initialCapacity)
        {
            Buffer = new byte[initialCapacity];
            _bufferMemoryHandle = Buffer.AsMemory().Pin();
            Length = 0;
        }

        /// <summary>
        /// Increases the capacity of the internal buffer.
        /// </summary>
        /// <param name="minExtraSize">The minimum needed extra buffer capacity.</param>
        public void ResizeBuffer(int minExtraSize)
        {
            _bufferMemoryHandle.Dispose();

            int newBufferSize = Math.Max(Buffer.Length * 2, Buffer.Length + minExtraSize);
            byte[] newBuffer = new byte[newBufferSize];
            Array.Copy(Buffer, newBuffer, Buffer.Length);

            Buffer = newBuffer;
            _bufferMemoryHandle = Buffer.AsMemory().Pin();
        }

        /// <summary>
        /// Writes a byte to the buffer.
        /// </summary>
        public void WriteByte(byte value)
        {
            if(Buffer.Length - Length < 1)
                ResizeBuffer(1);

            // Write and increase position
            Buffer[Length++] = value;
        }

        /// <summary>
        /// Writes a boolean to the buffer.
        /// </summary>
        public void WriteBoolean(bool value)
        {
            if(Buffer.Length - Length < 1)
                ResizeBuffer(1);

            // Write and increase position
            Buffer[Length++] = value ? (byte)1 : (byte)0;
        }

        /// <summary>
        /// Writes a char array to the buffer.
        /// </summary>
        public void WriteChars(char[] value)
        {
            if(Buffer.Length - Length < value.Length)
                ResizeBuffer(value.Length);

            // Write and increase position
            Encoding.ASCII.GetBytes(value, Buffer.AsSpan(Length));
            Length += value.Length;
        }

        /// <summary>
        /// Writes a signed 16-bit integer to the buffer.
        /// </summary>
        public unsafe void WriteInt16(short value)
        {
            if(Buffer.Length - Length < 2)
                ResizeBuffer(2);

            // Write and increase position
            Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + Length, value);
            Length += 2;
        }

        /// <summary>
        /// Writes an unsigned 16-bit integer to the buffer.
        /// </summary>
        public unsafe void WriteUInt16(ushort value)
        {
            if(Buffer.Length - Length < 2)
                ResizeBuffer(2);

            // Write and increase position
            Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + Length, value);
            Length += 2;
        }

        /// <summary>
        /// Writes a signed 32-bit integer to the buffer.
        /// </summary>
        public unsafe void WriteInt32(int value)
        {
            if(Buffer.Length - Length < 4)
                ResizeBuffer(4);

            // Write and increase position
            Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + Length, value);
            Length += 4;
        }

        /// <summary>
        /// Writes an unsigned 32-bit integer to the buffer.
        /// </summary>
        public unsafe void WriteUInt32(uint value)
        {
            if(Buffer.Length - Length < 4)
                ResizeBuffer(4);

            // Write and increase position
            Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + Length, value);
            Length += 4;
        }

        /// <summary>
        /// Writes a signed 64-bit integer to the buffer.
        /// </summary>
        public unsafe void WriteInt64(long value)
        {
            if(Buffer.Length - Length < 8)
                ResizeBuffer(8);

            // Write and increase position
            Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + Length, value);
            Length += 8;
        }

        /// <summary>
        /// Writes an unsigned 64-bit integer to the buffer.
        /// </summary>
        public unsafe void WriteUInt64(ulong value)
        {
            if(Buffer.Length - Length < 8)
                ResizeBuffer(8);

            // Write and increase position
            Unsafe.WriteUnaligned((byte*)_bufferMemoryHandle.Pointer + Length, value);
            Length += 8;
        }

        public void Dispose()
        {
            _bufferMemoryHandle.Dispose();
        }
    }
}