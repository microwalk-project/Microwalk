using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LeakageDetector
{
    /// <summary>
    /// Provides functions for fast reading from binary data.
    /// This class does not do error checking!
    /// </summary>
    class ByteArrayReader
    {
        /// <summary>
        /// The data to be read.
        /// </summary>
        private byte[] _buffer;

        /// <summary>
        /// The current read position.
        /// </summary>
        private int _position = 0;

        /// <summary>
        /// Returns or sets the current read position.
        /// </summary>
        public int Position
        {
            get { return _position; }
            set { _position = value; }
        }

        /// <summary>
        /// Loads the given file into the internal buffer.
        /// </summary>
        /// <param name="filename">The file to be loaded.</param>
        public ByteArrayReader(string filename)
        {
            // Read file
            _buffer = File.ReadAllBytes(filename);
        }

        /// <summary>
        /// Reads a byte from the buffer.
        /// </summary>
        /// <returns></returns>
        public byte ReadByte()
        {
            // Read and increase position
            return _buffer[_position++];
        }

        /// <summary>
        /// Reads a boolean from the buffer.
        /// </summary>
        /// <returns></returns>
        public bool ReadBoolean()
        {
            // Read and increase position
            return _buffer[_position++] != 0;
        }

        /// <summary>
        /// Reads an ANSI string from the buffer.
        /// </summary>
        /// <returns></returns>
        public unsafe string ReadString(int length)
        {
            // Read and increase position
            string str = null;
            fixed (byte* buf = &_buffer[_position])
                str = new string((sbyte*)buf, 0, length, Encoding.Default);
            _position += length;
            return str;
        }

        /// <summary>
        /// Reads a 32-bit integer from the buffer.
        /// </summary>
        /// <returns></returns>
        public unsafe int ReadInt32()
        {
            // Read and increase position
            int val;
            fixed (byte* buf = &_buffer[_position])
                if((_position & 0b11) == 0) // If the alignment is right, direct conversion is possible
                    val = *((int*)buf);
                else
                    val = (*buf) | (*(buf + 1) << 8) | (*(buf + 2) << 16) | (*(buf + 3) << 24); // Little Endian
            _position += 4;
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
            fixed (byte* buf = &_buffer[_position])
                if((_position & 0b111) == 0) // If the alignment is right, direct conversion is possible
                    val = *((long*)buf);
                else
                {
                    // Little Endian
                    int i1 = (*buf) | (*(buf + 1) << 8) | (*(buf + 2) << 16) | (*(buf + 3) << 24);
                    int i2 = (*(buf + 4)) | (*(buf + 5) << 8) | (*(buf + 6) << 16) | (*(buf + 7) << 24);
                    val = (uint)i1 | ((long)i2 << 32);
                }
            _position += 8;
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
    }
}
