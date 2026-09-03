using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace UDRoute
{
    // ==========================================
    // 2. 零拷贝网络基础层 (Udp Socket Wrapper)
    // ==========================================
    public class ZeroCopyUdpSocket : IDisposable
    {
        private readonly Socket _socket;
        public EndPoint LocalEndPoint => _socket.LocalEndPoint!;

        public ZeroCopyUdpSocket(int port)
        {
            _socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = true
            };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); // 忽略UDP Connection Reset
            }
            _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }

        public async ValueTask<(int length, EndPoint remoteEP)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), ct);
            return (result.ReceivedBytes, result.RemoteEndPoint);
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEP, CancellationToken ct)
        {
            await _socket.SendToAsync(buffer, SocketFlags.None, remoteEP, ct);
        }

        public void Dispose() => _socket.Dispose();
    }
}