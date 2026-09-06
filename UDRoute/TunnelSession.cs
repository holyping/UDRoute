using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using UDRoute.Logging;

namespace UDRoute
{
    public enum ChannelCmd : byte
    {
        Open = 1,
        Data = 2,
        Close = 3,
        KeepAlive = 4
    }

    public enum MuxType : byte
    {
        Udp = 1,       // 分支 1: 传送的 UDP 数据
        Kcp = 2,       // 分支 2: KCP 协议包 (承载 TCP 数据)
        KeepAlive = 3  // 隧道级心跳保活
    }

    // ==========================================
    // 8. 数据链路层 (NAT切换 & KCP可靠流转 & 信道复用)
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

        private readonly ConcurrentDictionary<uint, Channel<byte[]>> _tcpChannels = new();
        private int _activeChannelCount = 0;
        private uint _nextChannelId = 0;
        private int _kcpDriverStarted = 0;
        private readonly TaskCompletionSource _sessionClosedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _stateLock = new();
        private int _disposed = 0;

        public readonly TaskCompletionSource<bool> AuthTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid SessionId => _sessionId;
        public EndPoint ActiveRemoteEp => _activeRemoteEp;
        public bool IsDirect => _isDirect;
        public int Rtt => _isTcp ? (_kcp?.RxSrtt ?? 0) : _udpSrtt;
        public Channel<byte[]> InboundChannel => _inboundChannel;
        public CancellationToken SessionToken => _sessionCts.Token;
        public string ChannelDesc { get; set; } = string.Empty;
        public byte[]? PasswordHash { get; set; }
        public bool ForceRelay { get; set; }
        public int ReuseInterval { get; set; }
        public EndPoint? ProxyEp { get; set; }
        public int ActiveChannelCount => Volatile.Read(ref _activeChannelCount);
        public uint AllocateChannelId() => Interlocked.Increment(ref _nextChannelId);
        public Task SessionClosedTask => _sessionClosedTcs.Task;
        public bool IsClosed => _sessionCts.IsCancellationRequested;
        public Func<uint, Task>? OnIncomingChannel { get; set; }
        public Func<uint, byte[], Task>? OnIncomingUdpPacket { get; set; }
        public Action<uint, byte[]>? OnClientUdpDataReceived { get; set; }
        public Action<uint>? OnUdpChannelClosed { get; set; }
        public int IncrementActiveChannel()
        {
            lock (_stateLock)
            {
                int count = Interlocked.Increment(ref _activeChannelCount);
                UpdateActivity();
                return count;
            }
        }
        public int DecrementActiveChannel()
        {
            int count;
            bool shouldClose = false;
            lock (_stateLock)
            {
                count = Interlocked.Decrement(ref _activeChannelCount);
                if (count < 0)
                {
                    Interlocked.CompareExchange(ref _activeChannelCount, 0, count);
                    count = 0;
                }
                UpdateActivity();
                if (ReuseInterval <= 0 && count == 0 && !IsClosed)
                {
                    shouldClose = true;
                }
            }
            if (shouldClose)
            {
                _ = SendDisconnectAsync();
                Dispose();
            }
            return count;
        }

        public TunnelSession(ZeroCopyUdpSocket udpCore, EndPoint initialEp, Guid sessionId, int mtu, bool isTcp = true, KcpConfig? kcpConfig = null, int timeoutSeconds = 0, int reuseInterval = 0)
        {
            _udpCore = udpCore;
            _activeRemoteEp = initialEp;
            _sessionId = sessionId;
            _mtu = mtu;
            _isTcp = isTcp;
            _kcpConfig = kcpConfig ?? new KcpConfig();
            ReuseInterval = reuseInterval;

            if (reuseInterval > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!_sessionCts.IsCancellationRequested)
                        {
                            await Task.Delay(1000, _sessionCts.Token).ConfigureAwait(false);
                            bool shouldClose = false;
                            lock (_stateLock)
                            {
                                if (Volatile.Read(ref _activeChannelCount) == 0 && !IsClosed)
                                {
                                    long elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastActiveTime);
                                    if (elapsed > (long)reuseInterval * 1000)
                                    {
                                        shouldClose = true;
                                    }
                                }
                            }
                            if (shouldClose)
                            {
                                Log.Info($"[Tunnel] Session {_sessionId} idle for {reuseInterval}s without active channels. Closing.");
                                _ = SendDisconnectAsync();
                                Dispose();
                                break;
                            }
                        }
                    }
                    catch { }
                });
            }
            else if (timeoutSeconds > 0)
            {
                _ = Task.Run(async () =>
                {
                    int delay = Math.Min(5000, Math.Max(1000, timeoutSeconds * 1000 / 4));
                    try
                    {
                        while (!_sessionCts.IsCancellationRequested)
                        {
                            await Task.Delay(delay, _sessionCts.Token).ConfigureAwait(false);
                            bool shouldClose = false;
                            lock (_stateLock)
                            {
                                if (Volatile.Read(ref _activeChannelCount) == 0 && !IsClosed)
                                {
                                    long elapsed = Environment.TickCount64 - Interlocked.Read(ref _lastActiveTime);
                                    if (elapsed > (long)timeoutSeconds * 1000)
                                    {
                                        shouldClose = true;
                                    }
                                }
                            }
                            if (shouldClose)
                            {
                                Log.Info($"[Tunnel] Session {_sessionId} timeout after {timeoutSeconds}s of inactivity.");
                                _ = SendDisconnectAsync();
                                Dispose();
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
                    byte[] sendBuf = ArrayPool<byte>.Shared.Rent(kcpPacket.Length + 18);
                    try
                    {
                        sendBuf[0] = (byte)MsgType.Data;
                        _sessionId.TryWriteBytes(sendBuf.AsSpan(1, 16));
                        sendBuf[17] = (byte)MuxType.Kcp;
                        kcpPacket.Span.CopyTo(sendBuf.AsSpan(18));
                        var targetEp = _activeRemoteEp;
                        await _udpCore.SendAsync(sendBuf.AsMemory(0, kcpPacket.Length + 18), targetEp, _sessionCts.Token);
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

        public string? ServerT1 { get; set; }

        public async Task<bool> AuthenticateClientAsync(byte[] passwordHash, long sTimestamp, long pRecvTimeTicks, CancellationToken ct)
        {
            long tAuth = sTimestamp + (DateTime.UtcNow.Ticks - pRecvTimeTicks);
            
            // 第一次发送全0的AuthReq作为 Challenge 探测，向S端索取 t1
            byte[] req = new byte[57];
            req[0] = (byte)MsgType.AuthReq;
            _sessionId.TryWriteBytes(req.AsSpan(1, 16));
            BinaryPrimitives.WriteInt64LittleEndian(req.AsSpan(17, 8), tAuth);
            // authHash (25..56) 保持为 0

            using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            var token = linkCts.Token;

            // Start sending loop for Challenge
            var sendTask = Task.Run(async () =>
            {
                try
                {
                    while (ServerT1 == null && !AuthTcs.Task.IsCompleted && !token.IsCancellationRequested)
                    {
                        var targetEp = _activeRemoteEp;
                        await _udpCore.SendAsync(req, targetEp, token);
                        await Task.Delay(200, token);
                    }
                }
                catch { }
            }, token);

            try
            {
                // 等待 S 端回复带 t1 的 AuthRes (或者直接 AuthFail)
                int waitTime = 0;
                while (ServerT1 == null && waitTime < 5000 && !AuthTcs.Task.IsCompleted)
                {
                    await Task.Delay(100, token);
                    waitTime += 100;
                }
                
                if (AuthTcs.Task.IsCompleted) return await AuthTcs.Task;
                if (ServerT1 == null) return false;

                // 收到 t1，重新计算真实的 authHash
                string hash1 = "$SHA256$" + string.Concat(passwordHash.Select(b => b.ToString("x2")));
                byte[] hash2Bytes = ManagedSHA256.ComputeHashBytes(Encoding.UTF8.GetBytes(hash1 + ServerT1));
                
                byte[] hashInput = new byte[hash2Bytes.Length + 8];
                hash2Bytes.CopyTo(hashInput, 0);
                BinaryPrimitives.WriteInt64LittleEndian(hashInput.AsSpan(hash2Bytes.Length, 8), tAuth);
                byte[] authHash = ManagedSHA256.ComputeHashBytes(hashInput);
                authHash.CopyTo(req, 25);

                // 启动真实的 Auth 发送循环
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!AuthTcs.Task.IsCompleted && !token.IsCancellationRequested)
                        {
                            var targetEp = _activeRemoteEp;
                            await _udpCore.SendAsync(req, targetEp, token);
                            await Task.Delay(200, token);
                        }
                    }
                    catch { }
                }, token);

                await Task.WhenAny(AuthTcs.Task, Task.Delay(5000, token));
                return AuthTcs.Task.IsCompletedSuccessfully && AuthTcs.Task.Result;
            }
            finally
            {
                linkCts.Cancel();
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

        // 当收到来自非P端的对端直连通讯时，确保本端自动同步切为直连模式
        public void EnsureDirectRouteFromPeer(EndPoint remoteEp)
        {
            if (ForceRelay) return;
            if (ProxyEp != null && remoteEp.Equals(ProxyEp)) return;

            if (!_isDirect || !_activeRemoteEp.Equals(remoteEp))
            {
                Log.Info($"[Tunnel] Session {_sessionId} received direct signal from peer {remoteEp} (was {(_isDirect ? _activeRemoteEp : "Relay")}). Switched route to direct.");
                SwitchToDirect(remoteEp);
                NotifyDirectCommunicationEstablished();
            }
        }

        public void UpdateActivity()
        {
            Interlocked.Exchange(ref _lastActiveTime, Environment.TickCount64);
        }

        private int _relayEndNotified = 0;

        public void NotifyDirectCommunicationEstablished()
        {
            if (!_isDirect || ProxyEp == null) return;
            if (Interlocked.CompareExchange(ref _relayEndNotified, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    // 稍作等待（100ms），确保双方首批直连包稳定到达接管链路
                    await Task.Delay(100, _sessionCts.Token).ConfigureAwait(false);
                    byte[] endBuf = new byte[17];
                    endBuf[0] = (byte)MsgType.RelayEnd;
                    _sessionId.TryWriteBytes(endBuf.AsSpan(1, 16));

                    // 发送 RelayEnd 通知 P 端释放临时中继 session
                    for (int i = 0; i < 2; i++)
                    {
                        await _udpCore.SendAsync(endBuf, ProxyEp, CancellationToken.None).ConfigureAwait(false);
                        await Task.Delay(30).ConfigureAwait(false);
                    }
                    Log.Info($"[Tunnel] Session {_sessionId} direct punch communication confirmed. Notified Proxy {ProxyEp} to end relay session.");
                }
                catch { }
            });
        }

        // 接收来自 UDP 的数据包（载荷，进入复用协议层）
        public void OnUdpDataReceived(ReadOnlySpan<byte> payload)
        {
            UpdateActivity();
            if (_isDirect)
            {
                NotifyDirectCommunicationEstablished();
            }
            if (payload.Length == 0) return;

            byte muxType = payload[0];
            if (muxType == (byte)MuxType.Kcp)
            {
                // 分支 2: KCP 协议包 -> 传送的 TCP 数据
                if (_kcp != null)
                {
                    _kcp.Input(payload.Slice(1));
                    _signal.Release();
                }
            }
            else if (muxType == (byte)MuxType.Udp)
            {
                // 分支 1: 传送的 UDP 数据
                if (payload.Length >= 6)
                {
                    uint channelId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
                    byte cmd = payload[5];
                    var data = payload.Slice(6);
                    DispatchUdpFrame(channelId, (ChannelCmd)cmd, data);
                }
            }
            else if (muxType == (byte)MuxType.KeepAlive)
            {
                // 已调用 UpdateActivity()
            }
            else
            {
                // 兼容处理：若未带 MuxType
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
        }

        private void DispatchUdpFrame(uint channelId, ChannelCmd cmd, ReadOnlySpan<byte> data)
        {
            UpdateActivity();
            switch (cmd)
            {
                case ChannelCmd.Data:
                    byte[] copy = data.ToArray();
                    if (OnIncomingUdpPacket != null)
                    {
                        _ = Task.Run(() => OnIncomingUdpPacket(channelId, copy));
                    }
                    else if (OnClientUdpDataReceived != null)
                    {
                        OnClientUdpDataReceived(channelId, copy);
                    }
                    else
                    {
                        _inboundChannel.Writer.TryWrite(copy);
                    }
                    break;
                case ChannelCmd.Close:
                    OnUdpChannelClosed?.Invoke(channelId);
                    break;
            }
        }

        public async ValueTask SendUdpDataAsync(uint channelId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            UpdateActivity();
            int total = 17 + 1 + 4 + 1 + payload.Length;
            byte[] sendBuf = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                sendBuf[0] = (byte)MsgType.Data;
                _sessionId.TryWriteBytes(sendBuf.AsSpan(1, 16));
                sendBuf[17] = (byte)MuxType.Udp;
                BinaryPrimitives.WriteUInt32LittleEndian(sendBuf.AsSpan(18, 4), channelId);
                sendBuf[22] = (byte)ChannelCmd.Data;
                payload.Span.CopyTo(sendBuf.AsSpan(23));

                var targetEp = _activeRemoteEp;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
                await _udpCore.SendAsync(sendBuf.AsMemory(0, total), targetEp, linkedCts.Token);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sendBuf);
            }
        }

        public async ValueTask SendUdpCloseAsync(uint channelId, CancellationToken ct = default)
        {
            UpdateActivity();
            byte[] sendBuf = new byte[23];
            sendBuf[0] = (byte)MsgType.Data;
            _sessionId.TryWriteBytes(sendBuf.AsSpan(1, 16));
            sendBuf[17] = (byte)MuxType.Udp;
            BinaryPrimitives.WriteUInt32LittleEndian(sendBuf.AsSpan(18, 4), channelId);
            sendBuf[22] = (byte)ChannelCmd.Close;

            try
            {
                await _udpCore.SendAsync(sendBuf.AsMemory(), _activeRemoteEp, CancellationToken.None);
            }
            catch { }
        }

        public bool TryHandleDisconnect(EndPoint remoteEp)
        {
            // 防御性代码：如果当前 session 已经是直连模式，而断开信号来自 P 端或非直连对端，则忽略该信号
            if (_isDirect)
            {
                if (ProxyEp != null && remoteEp.Equals(ProxyEp))
                {
                    Log.Info($"[Tunnel] Session {_sessionId} is in direct mode. Ignored disconnect signal from Proxy {remoteEp}.");
                    return true;
                }
                if (!remoteEp.Equals(_activeRemoteEp))
                {
                    Log.Info($"[Tunnel] Session {_sessionId} is in direct mode. Ignored disconnect signal from non-direct endpoint {remoteEp}.");
                    return true;
                }
            }

            DisconnectReceived();
            return true;
        }

        public void DisconnectReceived()
        {
            if (!_sessionCts.IsCancellationRequested)
            {
                Log.Info($"[Tunnel] Session {_sessionId} disconnect received from peer.");
                _sessionCts.Cancel();
                _sessionClosedTcs.TrySetResult();
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

        public void StartKcpDriver(CancellationToken ct = default)
        {
            if (_kcp == null || Interlocked.CompareExchange(ref _kcpDriverStarted, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
                var token = linkedCts.Token;

                byte[] kcpRecvBuf = ArrayPool<byte>.Shared.Rent(65536);
                byte[] frameBuf = new byte[65536 * 2];
                int frameBufLen = 0;
                try
                {
                    int interval = _kcpConfig.Interval > 0 ? _kcpConfig.Interval : 10;
                    while (!token.IsCancellationRequested)
                    {
                        uint current = Kcp.CurrentTimeMs();
                        await _kcp.UpdateAsync(current);

                        if (_kcp.IsDeadLink)
                        {
                            Log.Warn($"[Tunnel] Session {_sessionId} KCP link dead (max retransmissions reached). Closing session.");
                            break;
                        }

                        while (true)
                        {
                            int len = _kcp.Recv(kcpRecvBuf);
                            if (len <= 0) break;

                            UpdateActivity();
                            if (frameBufLen + len > frameBuf.Length)
                            {
                                Array.Resize(ref frameBuf, Math.Max(frameBuf.Length * 2, frameBufLen + len));
                            }
                            Buffer.BlockCopy(kcpRecvBuf, 0, frameBuf, frameBufLen, len);
                            frameBufLen += len;

                            int offset = 0;
                            while (frameBufLen - offset >= 7)
                            {
                                uint chId = BinaryPrimitives.ReadUInt32LittleEndian(frameBuf.AsSpan(offset, 4));
                                byte cmdByte = frameBuf[offset + 4];
                                ushort pLen = BinaryPrimitives.ReadUInt16LittleEndian(frameBuf.AsSpan(offset + 5, 2));

                                if (frameBufLen - offset < 7 + pLen)
                                {
                                    break; // Wait for complete payload
                                }

                                byte[] payload = pLen > 0 ? frameBuf.AsSpan(offset + 7, pLen).ToArray() : Array.Empty<byte>();
                                offset += 7 + pLen;

                                DispatchFrame(chId, (ChannelCmd)cmdByte, payload);
                            }

                            if (offset > 0)
                            {
                                if (frameBufLen > offset)
                                {
                                    Buffer.BlockCopy(frameBuf, offset, frameBuf, 0, frameBufLen - offset);
                                }
                                frameBufLen -= offset;
                            }
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
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
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
                    _sessionClosedTcs.TrySetResult();
                    foreach (var kvp in _tcpChannels)
                    {
                        kvp.Value.Writer.TryComplete();
                    }
                }
            }, ct);
        }

        private void DispatchFrame(uint channelId, ChannelCmd cmd, byte[] payload)
        {
            UpdateActivity();
            switch (cmd)
            {
                case ChannelCmd.Open:
                    if (OnIncomingChannel != null)
                    {
                        var newCh = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
                        if (_tcpChannels.TryAdd(channelId, newCh))
                        {
                            IncrementActiveChannel();
                            _ = Task.Run(() => OnIncomingChannel(channelId));
                        }
                    }
                    break;
                case ChannelCmd.Data:
                    if (_tcpChannels.TryGetValue(channelId, out var dataCh))
                    {
                        dataCh.Writer.TryWrite(payload);
                    }
                    break;
                case ChannelCmd.Close:
                    if (_tcpChannels.TryGetValue(channelId, out var closeCh))
                    {
                        closeCh.Writer.TryComplete();
                    }
                    break;
                case ChannelCmd.KeepAlive:
                    break;
            }
        }

        public async ValueTask SendFrameAsync(uint channelId, ChannelCmd cmd, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (_kcp == null) return;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            var token = linkedCts.Token;

            int waitCount = 0;
            while (_kcp.WaitSnd > _kcpConfig.SndWnd * 2 && !token.IsCancellationRequested && waitCount < 500)
            {
                await Task.Delay(10, token);
                waitCount++;
            }

            int total = 7 + payload.Length;
            byte[] buf = ArrayPool<byte>.Shared.Rent(total);
            try
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), channelId);
                buf[4] = (byte)cmd;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(5, 2), (ushort)payload.Length);
                if (payload.Length > 0)
                {
                    payload.Span.CopyTo(buf.AsSpan(7));
                }

                UpdateActivity();
                _kcp.Send(buf.AsSpan(0, total));
                try { _signal.Release(); } catch { }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        public async Task RunChannelBridgeAsync(uint channelId, TcpClient tcp, CancellationToken ct)
        {
            StartKcpDriver(ct);

            var ch = _tcpChannels.GetOrAdd(channelId, _ =>
            {
                Interlocked.Increment(ref _activeChannelCount);
                return Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            });

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
            var token = linkedCts.Token;
            using var stream = tcp.GetStream();

            var tcpToKcpTask = Task.Run(async () =>
            {
                byte[] recvBuf = ArrayPool<byte>.Shared.Rent(_mtu);
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        int bytesRead = await stream.ReadAsync(recvBuf.AsMemory(0, _mtu), token);
                        if (bytesRead == 0) break; // Local TCP EOF

                        await SendFrameAsync(channelId, ChannelCmd.Data, recvBuf.AsMemory(0, bytesRead), token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || _sessionCts.IsCancellationRequested || !tcp.Connected)
                    {
                        // 正常断开
                    }
                    else if (ex is IOException ioEx && ioEx.InnerException is SocketException sex &&
                             (sex.SocketErrorCode == SocketError.OperationAborted ||
                              sex.SocketErrorCode == SocketError.ConnectionAborted ||
                              sex.SocketErrorCode == SocketError.ConnectionReset))
                    {
                        // 连接已中止
                    }
                    else
                    {
                        Log.Debug($"[Tunnel] Channel {channelId} TCP->KCP error: {ex.Message}");
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(recvBuf);
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        await SendFrameAsync(channelId, ChannelCmd.Close, ReadOnlyMemory<byte>.Empty, closeCts.Token);
                        int waitCount = 0;
                        while (_kcp?.WaitSnd > 0 && waitCount < 10 && !_sessionCts.IsCancellationRequested)
                        {
                            try { _signal.Release(); } catch { }
                            await Task.Delay(20, CancellationToken.None);
                            waitCount++;
                        }
                    }
                    catch { }
                }
            }, token);

            var kcpToTcpTask = Task.Run(async () =>
            {
                try
                {
                    while (await ch.Reader.WaitToReadAsync(token))
                    {
                        while (ch.Reader.TryRead(out var packet))
                        {
                            await stream.WriteAsync(packet, token);
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || _sessionCts.IsCancellationRequested || !tcp.Connected)
                    {
                        // 正常断开
                    }
                    else if (ex is IOException ioEx && ioEx.InnerException is SocketException sex &&
                             (sex.SocketErrorCode == SocketError.OperationAborted ||
                              sex.SocketErrorCode == SocketError.ConnectionAborted ||
                              sex.SocketErrorCode == SocketError.ConnectionReset))
                    {
                        // 连接已中止
                    }
                    else
                    {
                        Log.Debug($"[Tunnel] Channel {channelId} KCP->TCP error: {ex.Message}");
                    }
                }
                finally
                {
                    try { tcp.Close(); } catch { }
                }
            }, token);

            try
            {
                var completedTask = await Task.WhenAny(tcpToKcpTask, kcpToTcpTask);
                if (completedTask == tcpToKcpTask)
                {
                    // 若客户端套接字仍处于连接状态（半关闭状态），给予短暂缓冲允许服务端将剩余响应写回客户端
                    if (tcp.Connected)
                    {
                        await Task.WhenAny(kcpToTcpTask, Task.Delay(300, token));
                    }
                }
            }
            finally
            {
                try { tcp.Close(); } catch { }
                CloseChannel(channelId);
            }
        }

        public void CloseChannel(uint channelId)
        {
            if (_tcpChannels.TryRemove(channelId, out var ch))
            {
                ch.Writer.TryComplete();
                DecrementActiveChannel();
            }
        }

        public async Task OpenAndBridgeChannelAsync(TcpClient tcp, CancellationToken ct)
        {
            if (IsClosed)
            {
                throw new InvalidOperationException("TunnelSession is already closed.");
            }
            uint channelId = AllocateChannelId();
            await SendFrameAsync(channelId, ChannelCmd.Open, ReadOnlyMemory<byte>.Empty, ct);
            await RunChannelBridgeAsync(channelId, tcp, ct);
        }

        public Task RunTcpBridgeAsync(TcpClient tcp, CancellationToken ct) => OpenAndBridgeChannelAsync(tcp, ct);
        public Task RunTcpKcpBridgeAsync(TcpClient tcp, CancellationToken ct) => OpenAndBridgeChannelAsync(tcp, ct);

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
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _sessionCts.Cancel(); } catch { }
            _sessionClosedTcs.TrySetResult();
            // 出于高并发安全考虑，不显式 Dispose _signal 和 _sessionCts，交由 GC 回收以避免 ObjectDisposedException 竞争
        }
    }
}