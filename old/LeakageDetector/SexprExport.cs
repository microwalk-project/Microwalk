using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace LeakageDetector
{
    class SexprExport
    {
        public static byte[] EncodeParameters(RSAParameters key, byte[] cipher)
        {
            using(var stream = new MemoryStream())
            {
                var writer = new BinaryWriter(stream, Encoding.ASCII);

                // PRIVATE KEY
                StringBuilder keySexpr = new StringBuilder();
                keySexpr.AppendLine("(private-key");
                {
                    keySexpr.AppendLine("  (rsa");
                    {
                        keySexpr.AppendLine($"    (n #{BitConverter.ToString(key.Modulus).Replace("-","")}#)");
                        keySexpr.AppendLine($"    (e #{BitConverter.ToString(key.Exponent).Replace("-", "")}#)");
                        keySexpr.AppendLine($"    (d #{BitConverter.ToString(key.D).Replace("-", "")}#)");
                    }
                    keySexpr.AppendLine("  )");
                }
                keySexpr.AppendLine(")");
                writer.Write(keySexpr.Length);
                writer.Write(keySexpr.ToString().ToCharArray());

                // CIPHER TEXT
                StringBuilder cipherSexpr = new StringBuilder();
                cipherSexpr.AppendLine("(enc-val");
                {
                    cipherSexpr.AppendLine("  (rsa");
                    {
                        cipherSexpr.AppendLine($"    (flags no-blinding)");
                        cipherSexpr.AppendLine($"    (a #{BitConverter.ToString(cipher).Replace("-", "")}#)");
                    }
                    cipherSexpr.AppendLine("  )");
                }
                cipherSexpr.AppendLine(")");
                writer.Write(cipherSexpr.Length);
                writer.Write(cipherSexpr.ToString().ToCharArray());

                return stream.ToArray();
            }
        }
    }
}
