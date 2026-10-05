using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace UDRoute
{
    public static class ConfigProtector
    {
        // 静态缓存，进程生命周期内只计算一次，避免频繁的文件 I/O 和 Hash 计算
        private static readonly string _hashedMachineId = ComputeHashedMachineId();

        public static string GetMachineId()
        {
            return _hashedMachineId;
        }

        private static string ComputeHashedMachineId()
        {
            string rawId;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    if (File.Exists("/etc/machine-id")) rawId = File.ReadAllText("/etc/machine-id").Trim();
                    else if (File.Exists("/var/lib/dbus/machine-id")) rawId = File.ReadAllText("/var/lib/dbus/machine-id").Trim();
                    else rawId = Environment.MachineName + "_" + Environment.OSVersion.ToString();
                }
                else
                {
                    rawId = Environment.MachineName + "_" + Environment.OSVersion.ToString();
                }
            }
            catch
            {
                rawId = "UDRoute_Fallback_Machine_ID";
            }
            
            // t1 预先进行一次 Hash，保护机器特征不被明文抓包，同时对齐盐的长度为 44 位 Base64
            byte[] hashedIdBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(rawId));
            return Convert.ToBase64String(hashedIdBytes);
        }

        public static byte[] ComputeFastAuthHash(byte[] passwordHash, long timestamp, long pRecvTicks)
        {
            byte[] buffer = new byte[32 + 8 + 8];
            passwordHash.CopyTo(buffer.AsSpan(0, 32));
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32, 8), timestamp);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(40, 8), pRecvTicks);
            return ManagedSHA256.ComputeHashBytes(buffer);
        }

        public static string ComputeHWHash(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword) 
                || plainPassword.StartsWith("_HWHash_", StringComparison.OrdinalIgnoreCase)) 
                return plainPassword;
            
            // 密码B: 密码A以HASH256加密成密码B
            byte[] bBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(plainPassword));
            string B = "_HASH256_" + Convert.ToBase64String(bBytes);

            // t1: 机器特征码的HASH256
            string t1 = GetMachineId();
            
            // 密码C (HWHash): t1 + B 存为 Base64
            byte[] cBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(t1 + B));
            
            return "_HWHash_" + Convert.ToBase64String(cBytes);
        }
    }
}
