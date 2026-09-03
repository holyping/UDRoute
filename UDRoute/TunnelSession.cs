using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using UDRoute.Logging;

namespace UDRoute
{
    // ==========================================
    // 8. 数据链路层 (NAT切换 & KCP可靠流转)
    // ==========================================
    public class TunnelSession : IDisposable
    {
        private readonly ZeroCopyUdpSocket _udpCore;
        private volatile EndPoint _activeRemoteEp; // 动态EndPoint：初始为P，打洞成功后换为对方直接公网IP
        private readonly Guid _sessionId;
        private readonly int _mtu;
        private readonly bool _isTcp;
        private readonly KcpConfig _kcpConfig;
        private readonly Kcp? _kcp;
        private readonly SemaphoreSlim _signal = new(0);
        private readonly Channel<byte[]> _inboundChannel;
        private readonly CancellationTokenSource _sessionCts = new();
        private bool _isDirect = false;
        private long _lastActiveTime = Environment.TickCount64;
        private long _lastUdpOutTick = 0;
        private int _udpSrtt = 0;

        public Guid SessionId => _sessionId;
        public EndPoint ActiveRemoteEp => _activeRemoteEp;
        public bool IsDirect => _isDirect;
        public int Rtt => _isTcp ? (_kcp?.RxSrtt ?? 0) : _udpSrtt;
        public Channel<byte[]> InboundChannel => _inboundChannel;
        public CancellationToken SessionToken => _sessionCts.Token;
        public string ChannelDesc { get; set; } = string.Empty;

        public TunnelSession(ZeroCopyUdpSocket udpCore, EndPoint initialEp, Guid sessionId, int mtu, bool isTcp = true, KcpConfig? kcpConfig = null, int timeoutSeconds = 0)
        {
            _udpCore = udpCore;
            _activeRemoteEp = initialEp;
            _sessionId = sessionId;
            _mtu = mtu;
            _isTcp = isTcp;
            _kcpConfig = kcpConfig ?? new KcpConfig();

            if (timeoutSeconds > 0)
            {
                _ = Task.Run(async () =>
                {
                    int delay = timeoutSeconds * 1000 / 2;
                    if (delay < 1000) delay = 1000;
                    
                    try
                    {
                        while (!_sessionCts.IsCancellationRequested)
                        {
                            await Task.Delay(delay, _sessionCts.Token).ConfigureAwait(false);
                            long elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastActiveTime);
                            if (elapsed > timeoutSeconds * 1000)
                            {
                                Log.Info($"[Tunnel] Session {_sessionId} timeout after {timeoutSeconds}s of inactivity.");
                                _sessionCts.Cancel();
                                break;
                            }
                        }
                    }
                    catch { }
                });
            }

            _inboundChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            if (_isTcp)
            {
                uint conv = BinaryPrimitives.ReadUInt32LittleEndian(sessionId.ToByteArray().AsSpan(0, 4));
                _kcp = new Kcp(conv, async (kcpPacket) =>
                {
                    byte[] sendBuf = ArrayPool<byte>.Shared.Rent(kcpPacket.Length + 17);
                    try
                    {
                        sendBuf[0] = (byte)MsgType.Data;
                        _sessionId.TryWriteBytes(sendBuf.AsSpan(1, 16));
                        kcpPacket.Span.CopyTo(sendBuf.AsSpan(17));
                        var targetEp = _activeRemoteEp;
                        await _udpCore.SendAsync(sendBuf.AsMemory(0, kcpPacket.Length + 17), targetEp, _sessionCts.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log.Debug($"[Tunnel] KCP Output error: {ex.Message}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(sendBuf);
                    }
                });

                _kcp.SetMtu(_mtu);
                _kcp.SetNoDelay(_kcpConfig.NoDelay ? 1 : 0, _kcpConfig.Interval, _kcpConfig.Resend, _kcpConfig.Nc);
                _kcp.SetWindowSize(_kcpConfig.SndWnd, _kcpConfig.RcvWnd);
            }
        }

        // 当收到对方直接发来的打洞包，切换路由
        public void SwitchToDirect(EndPoint directEp)
        {
            if (!_isDirect || !_activeRemoteEp.Equals(directEp))
            {
                _activeRemoteEp = directEp;
                _isDirect = true;
                Log.Info($"[Tunnel] Session {_sessionId} route switched to direct: {directEp}");
            }
        }

        public void UpdateActivity()
        {
            Interlocked.Exchange(ref _lastActiveTime, Environment.TickCount64);
        }

        // 接收来自 UDP 的数据包（载荷）
        public void OnUdpDataReceived(ReadOnlySpan<byte> payload)
        {
            UpdateActivity();
            if (_isTcp && _kcp != null)
            {
                _kcp.Input(payload);
                _signal.Release();
            }
            else
            {
                byte[] copy = payload.ToArray();
                _inboundChannel.Writer.TryWrite(copy);
            }
        }

        public void DisconnectReceived()
        {
            if (!_sessionCts.IsCancellationRequested)
            {
                Log.Info($"[Tunnel] Session {_sessionId} disconnect received from peer.");
                _sessionCts.Cancel();
                _signal.Release();
            }
        }

        private async Task SendDisconnectAsync()
        {
            byte[] discBuf = new byte[17];
            discBuf[0] = (byte)MsgType.Disconnect;
            _sessionId.TryWriteBytes(discBuf.AsSpan(1, 16));
            
            for (int i = 0; i < 3; i++) // 冗余发送，防止UDP丢包
            {
                try
                {
                    await _udpCore.SendAsync(discBuf.AsMemory(), _activeRemoteEp, CancellationToken.None);
                    await Task.Delay(50);
                }
                catch { }
            }
        }

        public Task RunTcpKcpBridgeAsync(TcpClient tcp, CancellationToken ct) => RunTcpBridgeAsync(tcp, ct);

        // 双向桥接 TCP 客户端与目标端的数据链路
        public async Task RunTcpBridgeAsync(TcpClient tcp, CancellationToken ct)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            var token = linkedCts.Token;

            using var stream = tcp.GetStream();

            if (_kcp != null)
            {
                // 1. TCP 写入 KCP 发送队列
                var tcpToKcpTask = Task.Run(async () =>
                {
                    byte[] recvBuf = ArrayPool<byte>.Shared.Rent(_mtu);
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            int bytesRead = await stream.ReadAsync(recvBuf.AsMemory(0, _mtu), token);
                            if (bytesRead == 0) break;

                            UpdateActivity();
                            _kcp.Send(recvBuf.AsSpan(0, bytesRead));
                            try { _signal.Release(); } catch { }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Log.Debug($"[Tunnel] TCP->KCP error: {ex.Message}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(recvBuf);
                        _ = SendDisconnectAsync();
                        _sessionCts.Cancel();
                        try { _signal.Release(); } catch { }
                    }
                }, token);

                // 2. KCP 驱动循环与输出至 TCP
                var kcpDriverTask = Task.Run(async () =>
                {
                    byte[] kcpRecvBuf = ArrayPool<byte>.Shared.Rent(65535);
                    try
                    {
                        int interval = _kcpConfig.Interval > 0 ? _kcpConfig.Interval : 10;
                        while (!token.IsCancellationRequested)
                        {
                            uint current = Kcp.CurrentTimeMs();
                            await _kcp.UpdateAsync(current);

                            while (true)
                            {
                                int len = _kcp.Recv(kcpRecvBuf);
                                if (len <= 0) break;
                                await stream.WriteAsync(kcpRecvBuf.AsMemory(0, len), token);
                            }

                            uint nextTime = _kcp.Check(current);
                            int delay = (int)(nextTime - current);
                            if (delay < 1) delay = 1;
                            if (delay > interval) delay = interval;

                            try
                            {
                                await _signal.WaitAsync(delay, token);
                            }
                            catch (TimeoutException) { }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (ObjectDisposedException) { break; }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Log.Debug($"[Tunnel] KCP Driver error: {ex.Message}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(kcpRecvBuf);
                        _ = SendDisconnectAsync();
                        _sessionCts.Cancel();
                    }
                }, token);

                await Task.WhenAny(tcpToKcpTask, kcpDriverTask);
            }
            else
            {
                // 纯 TCP 模式原始桥接
                var udpToTcpTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await _inboundChannel.Reader.WaitToReadAsync(token))
                        {
                            while (_inboundChannel.Reader.TryRead(out var packet))
                            {
                                await stream.WriteAsync(packet, token);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    finally { 
                        _ = SendDisconnectAsync();
                        _sessionCts.Cancel(); 
                    }
                }, token);

                await udpToTcpTask;
            }
        }

        // 双向桥接目标 UDP 服务与 UDP 数据链路 (原始数据报零拷贝直传)
        public async Task RunUdpBridgeAsync(ZeroCopyUdpSocket targetSocket, IPEndPoint targetEp, CancellationToken ct)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            var token = linkedCts.Token;

            // 1. 隧道入站 -> 转发给目标 UDP 服务
            var tunnelToTargetTask = Task.Run(async () =>
            {
                try
                {
                    while (await _inboundChannel.Reader.WaitToReadAsync(token))
                    {
                        while (_inboundChannel.Reader.TryRead(out var packet))
                        {
                            await targetSocket.SendAsync(packet, targetEp, token);
                            
                            long lastTick = Interlocked.Read(ref _lastUdpOutTick);
                            if (lastTick > 0)
                            {
                                long delta = Environment.TickCount64 - lastTick;
                                if (delta > 0 && delta < 5000)
                                {
                                    if (_udpSrtt == 0) _udpSrtt = (int)delta;
                                    else _udpSrtt = (_udpSrtt * 7 + (int)delta) / 8;
                                }
                                Interlocked.Exchange(ref _lastUdpOutTick, 0); // Consumed
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Log.Debug($"[Tunnel] Tunnel->UDP Target error: {ex.Message}");
                }
                finally
                {
                    _ = SendDisconnectAsync();
                    _sessionCts.Cancel();
                }
            }, token);

            // 2. 目标 UDP 响应 -> 注入 MsgType.Data + SessionId 转发回对方
            var targetToTunnelTask = Task.Run(async () =>
            {
                byte[] recvBuf = ArrayPool<byte>.Shared.Rent(_mtu);
                byte[] sendBuf = ArrayPool<byte>.Shared.Rent(_mtu + 17);

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var (len, _) = await targetSocket.ReceiveAsync(recvBuf, token);
                        if (len <= 0) break;

                        UpdateActivity();
                        sendBuf[0] = (byte)MsgType.Data;
                        _sessionId.TryWriteBytes(sendBuf.AsSpan(1, 16));
                        recvBuf.AsSpan(0, len).CopyTo(sendBuf.AsSpan(17));

                        var targetRemoteEp = _activeRemoteEp;
                        await _udpCore.SendAsync(sendBuf.AsMemory(0, 17 + len), targetRemoteEp, token);
                        Interlocked.Exchange(ref _lastUdpOutTick, Environment.TickCount64);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Log.Debug($"[Tunnel] UDP Target->Tunnel error: {ex.Message}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(recvBuf);
                    ArrayPool<byte>.Shared.Return(sendBuf);
                    _ = SendDisconnectAsync();
                    _sessionCts.Cancel();
                }
            }, token);

            await Task.WhenAny(tunnelToTargetTask, targetToTunnelTask);
        }

        public void Dispose()
        {
            try { _sessionCts.Cancel(); } catch { }
            // 出于高并发安全考虑，不显式 Dispose _signal 和 _sessionCts，交由 GC 回收以避免 ObjectDisposedException 竞争
        }
    }
}