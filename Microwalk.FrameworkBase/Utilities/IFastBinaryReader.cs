namespace Microwalk.FrameworkBase.Utilities;

public interface IFastBinaryReader
{
    /// <summary>
    /// Returns or sets the current read position.
    /// </summary>
    int Position { get; set; }

    /// <summary>
    /// Total length of the binary data.
    /// </summary>
    int Length { get; }

    /// <summary>
    /// Reads a byte from the buffer.
    /// </summary>
    /// <returns></returns>
    byte ReadByte();

    /// <summary>
    /// Reads a boolean from the buffer.
    /// </summary>
    /// <returns></returns>
    bool ReadBoolean();

    /// <summary>
    /// Reads an ANSI string from the buffer.
    /// </summary>
    /// <returns></returns>
    string ReadString(int length);

    /// <summary>
    /// Reads a 16-bit integer from the buffer.
    /// </summary>
    /// <returns></returns>
    short ReadInt16();

    /// <summary>
    /// Reads a 32-bit integer from the buffer.
    /// </summary>
    /// <returns></returns>
    int ReadInt32();

    /// <summary>
    /// Reads a 32-bit unsigned integer from the buffer.
    /// </summary>
    /// <returns></returns>
    uint ReadUInt32();

    /// <summary>
    /// Reads a 64-bit integer from the buffer.
    /// </summary>
    /// <returns></returns>
    long ReadInt64();

    /// <summary>
    /// Reads a 64-bit unsigned integer from the buffer.
    /// </summary>
    /// <returns></returns>
    ulong ReadUInt64();

    void Dispose();
}