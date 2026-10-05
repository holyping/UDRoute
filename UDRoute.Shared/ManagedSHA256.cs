using System;
using System.Text;

namespace UDRoute
{
    /// <summary>
    /// A pure C# implementation of SHA256 to avoid libssl dependency on old Linux distros (like CentOS 7).
    /// </summary>
    public static class ManagedSHA256
    {
        private static readonly uint[] K = {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        };

        public static byte[] ComputeHashBytes(byte[] message)
        {
            uint[] H = {
                0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
                0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
            };

            ulong bitLen = (ulong)message.Length * 8;
            int padLen = 64 - ((message.Length + 8) % 64);
            if (padLen < 1) padLen += 64;

            byte[] padded = new byte[message.Length + padLen + 8];
            Buffer.BlockCopy(message, 0, padded, 0, message.Length);
            padded[message.Length] = 0x80;

            for (int i = 0; i < 8; i++)
                padded[padded.Length - 1 - i] = (byte)((bitLen >> (8 * i)) & 0xFF);

            uint[] W = new uint[64];
            for (int i = 0; i < padded.Length; i += 64)
            {
                for (int j = 0; j < 16; j++)
                {
                    W[j] = ((uint)padded[i + j * 4] << 24) |
                           ((uint)padded[i + j * 4 + 1] << 16) |
                           ((uint)padded[i + j * 4 + 2] << 8) |
                           ((uint)padded[i + j * 4 + 3]);
                }

                for (int j = 16; j < 64; j++)
                {
                    uint s0 = RightRotate(W[j - 15], 7) ^ RightRotate(W[j - 15], 18) ^ (W[j - 15] >> 3);
                    uint s1 = RightRotate(W[j - 2], 17) ^ RightRotate(W[j - 2], 19) ^ (W[j - 2] >> 10);
                    W[j] = W[j - 16] + s0 + W[j - 7] + s1;
                }

                uint a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];

                for (int j = 0; j < 64; j++)
                {
                    uint S1 = RightRotate(e, 6) ^ RightRotate(e, 11) ^ RightRotate(e, 25);
                    uint ch = (e & f) ^ (~e & g);
                    uint temp1 = h + S1 + ch + K[j] + W[j];
                    uint S0 = RightRotate(a, 2) ^ RightRotate(a, 13) ^ RightRotate(a, 22);
                    uint maj = (a & b) ^ (a & c) ^ (b & c);
                    uint temp2 = S0 + maj;

                    h = g; g = f; f = e; e = d + temp1;
                    d = c; c = b; b = a; a = temp1 + temp2;
                }

                H[0] += a; H[1] += b; H[2] += c; H[3] += d;
                H[4] += e; H[5] += f; H[6] += g; H[7] += h;
            }

            byte[] result = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                result[i * 4] = (byte)(H[i] >> 24);
                result[i * 4 + 1] = (byte)(H[i] >> 16);
                result[i * 4 + 2] = (byte)(H[i] >> 8);
                result[i * 4 + 3] = (byte)(H[i]);
            }
            return result;
        }

        public static string ComputeHash(string text)
        {
            byte[] bytes = ComputeHashBytes(Encoding.UTF8.GetBytes(text));
            StringBuilder sb = new StringBuilder(64);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static uint RightRotate(uint x, int n) => (x >> n) | (x << (32 - n));
    }
}

