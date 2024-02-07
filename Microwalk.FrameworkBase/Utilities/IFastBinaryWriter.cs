namespace Microwalk.FrameworkBase.Utilities;

public interface IFastBinaryWriter
{
    /// <summary>
    /// Writes a byte to the buffer.
    /// </summary>
    void WriteByte(byte value);

    /// <summary>
    /// Writes a boolean to the buffer.
    /// </summary>
    void WriteBoolean(bool value);

    /// <summary>
    /// Writes a char array to the buffer.
    /// </summary>
    void WriteChars(char[] value);

    /// <summary>
    /// Writes a signed 16-bit integer to the buffer.
    /// </summary>
    void WriteInt16(short value);

    /// <summary>
    /// Writes an unsigned 16-bit integer to the buffer.
    /// </summary>
    void WriteUInt16(ushort value);

    /// <summary>
    /// Writes a signed 32-bit integer to the buffer.
    /// </summary>
    void WriteInt32(int value);

    /// <summary>
    /// Writes an unsigned 32-bit integer to the buffer.
    /// </summary>
    void WriteUInt32(uint value);

    /// <summary>
    /// Writes a signed 64-bit integer to the buffer.
    /// </summary>
    void WriteInt64(long value);

    /// <summary>
    /// Writes an unsigned 64-bit integer to the buffer.
    /// </summary>
    void WriteUInt64(ulong value);
}