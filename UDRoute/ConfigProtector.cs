using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace UDRoute
{
    public static class ConfigProtector
    {
        public static string GetMachineId(Guid devId)
        {
            // 使用配置中的 DevId 作为稳定的 t1，避免跨机器复制配置文件或 Docker 容器重启导致的 machine-id 漂移
            byte[] hashedIdBytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(devId.ToString()));
            return Convert.ToBase64String(hashedIdBytes);
        }

        public static string ComputeHWHash(string plainPassword, Guid devId)
        {
            if (string.IsNullOrEmpty(plainPassword) || plainPassword.StartsWith("$HWHash$")) 
                return plainPassword;
            
            // P端保存的 hash1
            string hash1 = "$SHA256$" + ManagedSHA256.ComputeHash(plainPassword);
            string t1 = GetMachineId(devId);
            
            // S端保存的 hash2 = HashBytes(hash1 + t1) 存为 Base64
            byte[] hash2Bytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(hash1 + t1));
            
            return "$HWHash$" + Convert.ToBase64String(hash2Bytes);
        }
    }
}
