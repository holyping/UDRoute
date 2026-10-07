using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

class Program {
    static void Main() {
        string path = @""..\UDRoute.Shared\ProtocolHelper.cs"";
        string content = File.ReadAllText(path, Encoding.UTF8);

        // 1. ResolveEndPointAsync
        content = content.Replace(
            ""var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);\r\n                if (addresses.Length > 0)\r\n                {\r\n                    return new IPEndPoint(addresses[0], port);\r\n                }"",
            ""var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);\r\n                if (DisableIPv6) addresses = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));\r\n                if (addresses.Length > 0)\r\n                {\r\n                    return new IPEndPoint(addresses[0], port);\r\n                }""
        );

        // 2. ResolveAllEndPointsAsync
        content = content.Replace(
            ""var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);\r\n                var eps = new List<IPEndPoint>();\r\n                foreach (var a in addresses)"",
            ""var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);\r\n                if (DisableIPv6) addresses = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(addresses, a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));\r\n                var eps = new List<IPEndPoint>();\r\n                foreach (var a in addresses)""
        );

        // 3. GetLocalEndPoints
        content = content.Replace(
            ""if (ipInfo.Address.AddressFamily == AddressFamily.InterNetwork || ipInfo.Address.AddressFamily == AddressFamily.InterNetworkV6)"",
            ""if (ipInfo.Address.AddressFamily == AddressFamily.InterNetwork || (!DisableIPv6 && ipInfo.Address.AddressFamily == AddressFamily.InterNetworkV6))""
        );

        // 4. GetLocalIPAddresses
        content = content.Replace(
            ""if (!IPAddress.IsLoopback(ipInfo.Address) &&\r\n                            (ipInfo.Address.AddressFamily == AddressFamily.InterNetwork || ipInfo.Address.AddressFamily == AddressFamily.InterNetworkV6))"",
            ""if (!IPAddress.IsLoopback(ipInfo.Address) &&\r\n                            (ipInfo.Address.AddressFamily == AddressFamily.InterNetwork || (!DisableIPv6 && ipInfo.Address.AddressFamily == AddressFamily.InterNetworkV6)))""
        );

        File.WriteAllText(path, content, Encoding.UTF8);
    }
}
