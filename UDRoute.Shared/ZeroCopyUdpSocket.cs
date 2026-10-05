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
        private readonly SemaphoreSlim _sendLock = new(1, 1);
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
            
            // 增大系统收发缓冲区，应对大窗口的高吞吐量 (避免Burst导致丢包)
            _socket.ReceiveBufferSize = 2 * 1024 * 1024;
            _socket.SendBufferSize = 2 * 1024 * 1024;

            _socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }

        public async ValueTask<(int length, EndPoint remoteEP)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), ct);
            var ep = result.RemoteEndPoint;
            if (ep is IPEndPoint ipEp && ipEp.Address.IsIPv4MappedToIPv6)
            {
                ep = new IPEndPoint(ipEp.Address.MapToIPv4(), ipEp.Port);
            }
            return (result.ReceivedBytes, ep);
        }

        public async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, EndPoint remoteEP, CancellationToken ct)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                await _socket.SendToAsync(buffer, SocketFlags.None, remoteEP, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _socket.Dispose();
            _sendLock.Dispose();
        }
    }
}