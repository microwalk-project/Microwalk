using System;
using System.Collections.Generic;
using System.Linq;

namespace Microwalk.FrameworkBase.Utilities
{
    /// <summary>
    /// Helper class for hashing and comparing byte arrays.
    /// Note: This class is optimized for *random* byte arrays (e.g., hashes). Using this with uniform byte arrays may yield unexpected results.
    /// </summary>
    public class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => (x ?? Array.Empty<byte>()).SequenceEqual(y ?? Array.Empty<byte>());

        public int GetHashCode(byte[] obj)
        {
            // Simply return the most significant 4 bytes; if the byte arrays are random enough, this should have low collision probability
            uint hash = 0;
            for(int i = 0; i < Math.Min(4, obj.Length); ++i)
                hash ^= (uint)(obj[i] << (8 * i));
            return unchecked((int)hash);
        }
    }
}