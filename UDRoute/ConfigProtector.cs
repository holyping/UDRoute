using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

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

        public static string ComputeHWHash(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword) || plainPassword.StartsWith("$HWHash$")) 
                return plainPassword;
            
            // P端保存的 hash1
            string hash1 = "$SHA256$" + ManagedSHA256.ComputeHash(plainPassword);
            string t1 = GetMachineId();
            
            // S端保存的 hash2 = HashBytes(hash1 + t1) 存为 Base64
            byte[] hash2Bytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(hash1 + t1));
            
            return "$HWHash$" + Convert.ToBase64String(hash2Bytes);
        }
    }
}
