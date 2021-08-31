using System;
using System.IO;
using System.Text;

namespace Microwalk.FrameworkBase.Utilities
{
    /// <summary>
    /// Provides functions for fast reading from binary data.
    /// This class does not do error checking!
    /// </summary>
    public class FastBinaryReader : IDisposable
    {
        /// <summary>
        /// Returns or sets the current read position.
        /// </summary>
        public int Position { get; set; }

        /// <summary>
        /// The byte buffer this object reads from.
        /// </summary>
        public Memory<byte> Buffer { get; }

        /// <summary>
        /// Loads the given file into the internal buffer.
        /// </summary>
        /// <param name="filename">The file to be loaded.</param>
        public FastBinaryReader(string filename)
        {
            Buffer = File.ReadAllBytes(filename);
        }

        /// <summary>
        /// Creates a new reader for the given byte buffer.
        /// </summary>
        /// <param name="buffer">Buffer containing the data to be read.</param>
        public FastBinaryReader(Memory<byte> buffer)
        {
            Buffer = buffer;
        }

        /// <summary>
        /// Reads a byte from the buffer.
        /// </summary>
        /// <returns></returns>
        public byte ReadByte()
        {
            // Read and increase position
            return Buffer.Span[Position++];
        }

        /// <summary>
        /// Reads a boolean from the buffer.
        /// </summary>
        /// <returns></returns>
        public bool ReadBoolean()
        {
            // Read and increase position
            return Buffer.Span[Position++] != 0;
        }

        /// <summary>
        /// Reads an ANSI string from the buffer.
        /// </summary>
        /// <returns></returns>
        public unsafe string ReadString(int length)
        {
            // Read and increase position
            string str;
            fixed(byte* buf = &Buffer.Span[Position])
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
            // Read and increase position
            short val;
            fixed(byte* buf = &Buffer.Span[Position])
                if((Position & 0b1) == 0) // If the alignment is right, direct conversion is possible
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
            // Read and increase position
            int val;
            fixed(byte* buf = &Buffer.Span[Position])
                if((Position & 0b11) == 0) // If the alignment is right, direct conversion is possible
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
            // Read and increase position
            long val;
            fixed(byte* buf = &Buffer.Span[Position])
                if((Position & 0b111) == 0) // If the alignment is right, direct conversion is possible
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
            // Nothing to do here for now
        }
    }
}