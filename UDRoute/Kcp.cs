using System.Buffers;
using System.Buffers.Binary;

namespace UDRoute
{
    // ==========================================
    // KCP 协议核心实现 (ARQ 可靠传输状态机)
    // ==========================================
    public class Kcp
    {
        public const int IKCP_RTO_NDL = 30;  // no delay min rto
        public const int IKCP_RTO_MIN = 100; // normal min rto
        public const int IKCP_RTO_DEF = 200;
        public const int IKCP_RTO_MAX = 60000;

        public const byte IKCP_CMD_PUSH = 81; // cmd: push data
        public const byte IKCP_CMD_ACK = 82;  // cmd: ack
        public const byte IKCP_CMD_WASK = 83; // cmd: window probe (ask)
        public const byte IKCP_CMD_WINS = 84; // cmd: window size (tell)

        public const int IKCP_ASK_SEND = 1; // need to send IKCP_CMD_WASK
        public const int IKCP_ASK_TELL = 2; // need to send IKCP_CMD_WINS

        public const int IKCP_WND_SND = 32;
        public const int IKCP_WND_RCV = 128;
        public const int IKCP_MTU_DEF = 1400;
        public const int IKCP_ACK_FAST = 3;
        public const int IKCP_INTERVAL = 100;
        public const int IKCP_OVERHEAD = 24;
        public const int IKCP_DEADLINK = 20;
        public const int IKCP_THRESH_INIT = 2;
        public const int IKCP_THRESH_MIN = 2;
        public const int IKCP_PROBE_INIT = 200;    // 200 ms to probe window size (reduced from 7000ms to fix TCP stalls)
        public const int IKCP_PROBE_LIMIT = 10000; // up to 10 secs to probe window

        public class Segment
        {
            public uint Conv;
            public byte Cmd;
            public byte Frg;
            public ushort Wnd;
            public uint Ts;
            public uint Sn;
            public uint Una;
            public uint ResendTs;
            public uint Rto;
            public uint FastAck;
            public uint Xmit;
            public byte[]? Data;
            public int Len;

            public void Encode(Span<byte> ptr)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(ptr.Slice(0, 4), Conv);
                ptr[4] = Cmd;
                ptr[5] = Frg;
                BinaryPrimitives.WriteUInt16LittleEndian(ptr.Slice(6, 2), Wnd);
                BinaryPrimitives.WriteUInt32LittleEndian(ptr.Slice(8, 4), Ts);
                BinaryPrimitives.WriteUInt32LittleEndian(ptr.Slice(12, 4), Sn);
                BinaryPrimitives.WriteUInt32LittleEndian(ptr.Slice(16, 4), Una);
                BinaryPrimitives.WriteInt32LittleEndian(ptr.Slice(20, 4), Len);
            }
        }

        private readonly uint _conv;
        private int _mtu = IKCP_MTU_DEF;
        private int _mss = IKCP_MTU_DEF - IKCP_OVERHEAD;
        private int _state = 0;

        public int State => _state;
        public bool IsDeadLink => _state == -1;
        public int WaitSnd { get { lock (_lock) { return _sndBuf.Count + _sndQueue.Count; } } }

        private uint _sndUna = 0;
        private uint _sndNxt = 0;
        private uint _rcvNxt = 0;
        private uint _ssthresh = IKCP_THRESH_INIT;

        private int _rxRttVal = 0;
        private int _rxSrtt = 0;
        private int _rxRto = IKCP_RTO_DEF;
        private int _rxMinRto = IKCP_RTO_MIN;

        // 对外暴露的平滑往返延迟 (Ping)
        public int RxSrtt => _rxSrtt;

        private int _sndWnd = IKCP_WND_SND;
        private int _rcvWnd = IKCP_WND_RCV;
        private int _rmtWnd = IKCP_WND_RCV;
        private int _cwnd = 0;
        private int _incr = 0;
        private int _probe = 0;

        private int _interval = IKCP_INTERVAL;
        private uint _tsFlush = IKCP_INTERVAL;
        private int _nodelay = 0;
        private int _updated = 0;
        private uint _tsProbe = 0;
        private int _probeWait = 0;
        private int _deadLink = IKCP_DEADLINK;
        private int _fastResend = 0;
        private int _nocwnd = 0;
        private bool _stream = true; // 流模式，聚合小包

        private readonly List<Segment> _sndQueue = new();
        private readonly List<Segment> _rcvQueue = new();
        private readonly List<Segment> _sndBuf = new();
        private readonly List<Segment> _rcvBuf = new();
        private readonly List<(uint sn, uint ts)> _ackList = new();

        private readonly byte[] _buffer;
        private readonly Func<ReadOnlyMemory<byte>, ValueTask> _output;
        private readonly object _lock = new();

        public Kcp(uint conv, Func<ReadOnlyMemory<byte>, ValueTask> output)
        {
            _conv = conv;
            _output = output;
            _buffer = new byte[(_mtu + IKCP_OVERHEAD) * 3];
        }

        private static int TimeDiff(uint later, uint earlier) => (int)(later - earlier);

        public void SetNoDelay(int nodelay, int interval, int resend, int nc)
        {
            lock (_lock)
            {
                _nodelay = nodelay;
                _rxMinRto = nodelay > 0 ? IKCP_RTO_NDL : IKCP_RTO_MIN;
                _interval = interval >= 10 ? (interval <= 5000 ? interval : 5000) : 10;
                _fastResend = resend;
                _nocwnd = nc;
            }
        }

        public void SetWindowSize(int sndwnd, int rcvwnd)
        {
            lock (_lock)
            {
                if (sndwnd > 0) _sndWnd = sndwnd;
                if (rcvwnd > 0) _rcvWnd = Math.Max(rcvwnd, IKCP_WND_RCV);
            }
        }

        public void SetMtu(int mtu)
        {
            lock (_lock)
            {
                if (mtu < 50 || mtu < IKCP_OVERHEAD) return;
                _mtu = mtu;
                _mss = _mtu - IKCP_OVERHEAD;
            }
        }

        public int PeekSize()
        {
            lock (_lock)
            {
                if (_rcvQueue.Count == 0) return -1;
                var seq = _rcvQueue[0];
                if (seq.Frg == 0) return seq.Len;
                if (_rcvQueue.Count < seq.Frg + 1) return -1;

                int length = 0;
                for (int i = 0; i < _rcvQueue.Count; i++)
                {
                    var item = _rcvQueue[i];
                    length += item.Len;
                    if (item.Frg == 0) break;
                }
                return length;
            }
        }

        public int Recv(Span<byte> buffer)
        {
            lock (_lock)
            {
                if (_rcvQueue.Count == 0) return -1;
                int peekSize = PeekSize();
                if (peekSize < 0) return -2;
                if (buffer.Length < peekSize) return -3;

                bool isStream = _stream;
                int count = 0;
                int offset = 0;

                for (int i = 0; i < _rcvQueue.Count; i++)
                {
                    var seg = _rcvQueue[i];
                    if (seg.Data != null && seg.Len > 0)
                    {
                        seg.Data.AsSpan(0, seg.Len).CopyTo(buffer.Slice(offset));
                        offset += seg.Len;
                    }
                    count++;
                    if (seg.Frg == 0) break;
                }

                _rcvQueue.RemoveRange(0, count);

                // 将 rcvBuf 中连续的数据包移入 rcvQueue
                while (_rcvBuf.Count > 0)
                {
                    var seg = _rcvBuf[0];
                    if (seg.Sn == _rcvNxt && _rcvQueue.Count < _rcvWnd)
                    {
                        _rcvBuf.RemoveAt(0);
                        _rcvQueue.Add(seg);
                        _rcvNxt++;
                    }
                    else
                    {
                        break;
                    }
                }

                return offset;
            }
        }

        public int Send(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0) return -1;

            lock (_lock)
            {
                int count;
                if (buffer.Length <= _mss)
                    count = 1;
                else
                    count = (buffer.Length + _mss - 1) / _mss;

                if (count >= 255) return -2;
                if (count == 0) count = 1;

                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    int size = Math.Min(buffer.Length - offset, _mss);
                    var seg = new Segment
                    {
                        Conv = _conv,
                        Cmd = IKCP_CMD_PUSH,
                        Frg = (byte)(_stream ? 0 : (count - i - 1)),
                        Len = size,
                        Data = new byte[size]
                    };
                    buffer.Slice(offset, size).CopyTo(seg.Data);
                    _sndQueue.Add(seg);
                    offset += size;
                }

                return 0;
            }
        }

        public int Input(ReadOnlySpan<byte> data)
        {
            lock (_lock)
            {
                uint maxAck = 0;
                bool flag = false;
                uint prevUna = _sndUna;

                if (data.Length < IKCP_OVERHEAD) return -1;

                int offset = 0;
                while (data.Length - offset >= IKCP_OVERHEAD)
                {
                    var ptr = data.Slice(offset);
                    uint conv = BinaryPrimitives.ReadUInt32LittleEndian(ptr.Slice(0, 4));
                    if (conv != _conv) return -1;

                    byte cmd = ptr[4];
                    byte frg = ptr[5];
                    ushort wnd = BinaryPrimitives.ReadUInt16LittleEndian(ptr.Slice(6, 2));
                    uint ts = BinaryPrimitives.ReadUInt32LittleEndian(ptr.Slice(8, 4));
                    uint sn = BinaryPrimitives.ReadUInt32LittleEndian(ptr.Slice(12, 4));
                    uint una = BinaryPrimitives.ReadUInt32LittleEndian(ptr.Slice(16, 4));
                    int len = BinaryPrimitives.ReadInt32LittleEndian(ptr.Slice(20, 4));

                    offset += IKCP_OVERHEAD;
                    if (data.Length - offset < len || len < 0) return -2;

                    if (cmd != IKCP_CMD_PUSH && cmd != IKCP_CMD_ACK && cmd != IKCP_CMD_WASK && cmd != IKCP_CMD_WINS)
                        return -3;

                    _rmtWnd = wnd;
                    ParseUna(una);
                    ShrinkBuf();

                    if (cmd == IKCP_CMD_ACK)
                    {
                        if (TimeDiff(CurrentTimeMs(), ts) >= 0)
                        {
                            UpdateAck(TimeDiff(CurrentTimeMs(), ts));
                        }
                        ParseAck(sn);
                        ShrinkBuf();
                        if (!flag)
                        {
                            flag = true;
                            maxAck = sn;
                        }
                        else if (TimeDiff(sn, maxAck) > 0)
                        {
                            maxAck = sn;
                        }
                    }
                    else if (cmd == IKCP_CMD_PUSH)
                    {
                        if (TimeDiff(sn, _rcvNxt + (uint)_rcvWnd) < 0)
                        {
                            _ackList.Add((sn, ts));
                            if (TimeDiff(sn, _rcvNxt) >= 0)
                            {
                                var seg = new Segment
                                {
                                    Conv = conv,
                                    Cmd = cmd,
                                    Frg = frg,
                                    Wnd = wnd,
                                    Ts = ts,
                                    Sn = sn,
                                    Una = una,
                                    Len = len
                                };
                                if (len > 0)
                                {
                                    seg.Data = new byte[len];
                                    data.Slice(offset, len).CopyTo(seg.Data);
                                }
                                ParseData(seg);
                            }
                        }
                    }
                    else if (cmd == IKCP_CMD_WASK)
                    {
                        _probe |= IKCP_ASK_TELL;
                    }
                    else if (cmd == IKCP_CMD_WINS)
                    {
                        // do nothing
                    }

                    offset += len;
                }

                if (flag)
                {
                    ParseFastAck(maxAck);
                }

                if (TimeDiff(_sndUna, prevUna) > 0)
                {
                    if (_cwnd < _rmtWnd)
                    {
                        int mss = _mss;
                        if (_cwnd < _ssthresh)
                        {
                            _cwnd++;
                            _incr += mss;
                        }
                        else
                        {
                            if (_incr == 0)
                            {
                                _incr = Math.Max(mss, 1);
                            }
                            _incr += (mss * mss) / _incr + (mss / 16);
                            if ((_cwnd + 1) * mss <= _incr)
                            {
                                _cwnd = (_incr + mss - 1) / ((mss > 0) ? mss : 1);
                            }
                        }
                        if (_cwnd > _rmtWnd)
                        {
                            _cwnd = _rmtWnd;
                            _incr = _rmtWnd * mss;
                        }
                    }
                }

                return 0;
            }
        }

        private void ParseData(Segment newSeg)
        {
            uint sn = newSeg.Sn;
            if (TimeDiff(sn, _rcvNxt + (uint)_rcvWnd) >= 0 || TimeDiff(sn, _rcvNxt) < 0)
                return;

            int n = _rcvBuf.Count;
            int repeat = 0;
            int insertIdx = n;

            for (int i = n - 1; i >= 0; i--)
            {
                var seg = _rcvBuf[i];
                if (seg.Sn == sn)
                {
                    repeat = 1;
                    break;
                }
                if (TimeDiff(sn, seg.Sn) > 0)
                {
                    insertIdx = i + 1;
                    break;
                }
            }

            if (repeat == 0)
            {
                _rcvBuf.Insert(insertIdx, newSeg);
            }

            // 移动连续包至 rcvQueue
            while (_rcvBuf.Count > 0)
            {
                var seg = _rcvBuf[0];
                if (seg.Sn == _rcvNxt && _rcvQueue.Count < _rcvWnd)
                {
                    _rcvBuf.RemoveAt(0);
                    _rcvQueue.Add(seg);
                    _rcvNxt++;
                }
                else
                {
                    break;
                }
            }
        }

        private void ParseUna(uint una)
        {
            int count = 0;
            for (int i = 0; i < _sndBuf.Count; i++)
            {
                var seg = _sndBuf[i];
                if (TimeDiff(una, seg.Sn) > 0)
                    count++;
                else
                    break;
            }
            if (count > 0)
                _sndBuf.RemoveRange(0, count);
        }

        private void ParseAck(uint sn)
        {
            if (TimeDiff(sn, _sndUna) < 0 || TimeDiff(sn, _sndNxt) >= 0) return;

            for (int i = 0; i < _sndBuf.Count; i++)
            {
                var seg = _sndBuf[i];
                if (sn == seg.Sn)
                {
                    _sndBuf.RemoveAt(i);
                    break;
                }
                if (TimeDiff(sn, seg.Sn) < 0) break;
            }
        }

        private void ParseFastAck(uint sn)
        {
            if (TimeDiff(sn, _sndUna) < 0 || TimeDiff(sn, _sndNxt) >= 0) return;

            for (int i = 0; i < _sndBuf.Count; i++)
            {
                var seg = _sndBuf[i];
                if (TimeDiff(sn, seg.Sn) < 0) break;
                else if (sn != seg.Sn)
                {
                    seg.FastAck++;
                }
            }
        }

        private void ShrinkBuf()
        {
            _sndUna = _sndBuf.Count > 0 ? _sndBuf[0].Sn : _sndNxt;
        }

        private void UpdateAck(int rtt)
        {
            if (_rxSrtt == 0)
            {
                _rxSrtt = rtt;
                _rxRttVal = rtt / 2;
            }
            else
            {
                int delta = rtt - _rxSrtt;
                if (delta < 0) delta = -delta;
                _rxRttVal = (3 * _rxRttVal + delta) / 4;
                _rxSrtt = (7 * _rxSrtt + rtt) / 8;
                if (_rxSrtt < 1) _rxSrtt = 1;
            }
            int rto = _rxSrtt + Math.Max(_interval, 4 * _rxRttVal);
            _rxRto = Math.Clamp(rto, _rxMinRto, IKCP_RTO_MAX);
        }

        public async ValueTask UpdateAsync(uint currentMs)
        {
            int slap = TimeDiff(currentMs, _tsFlush);
            if (_updated == 0)
            {
                _updated = 1;
                _tsFlush = currentMs;
            }

            if (slap >= 10000 || slap < -10000)
            {
                _tsFlush = currentMs;
                slap = 0;
            }

            if (slap >= 0)
            {
                _tsFlush += (uint)_interval;
                if (TimeDiff(currentMs, _tsFlush) >= 0)
                {
                    _tsFlush = currentMs + (uint)_interval;
                }
                await FlushAsync();
            }
        }

        public uint Check(uint currentMs)
        {
            lock (_lock)
            {
                uint tsFlush = _tsFlush;
                int tmPacket = 0x7fffffff;
                if (_updated == 0) return currentMs;

                if (TimeDiff(currentMs, tsFlush) >= 10000 || TimeDiff(currentMs, tsFlush) < -10000)
                {
                    tsFlush = currentMs;
                }

                if (TimeDiff(currentMs, tsFlush) >= 0)
                {
                    return currentMs;
                }

                int tmFlush = TimeDiff(tsFlush, currentMs);
                for (int i = 0; i < _sndBuf.Count; i++)
                {
                    var seg = _sndBuf[i];
                    int diff = TimeDiff(seg.ResendTs, currentMs);
                    if (diff <= 0) return currentMs;
                    if (diff < tmPacket) tmPacket = diff;
                }

                uint minimal = (uint)(tmPacket < tmFlush ? tmPacket : tmFlush);
                if (minimal >= (uint)_interval) minimal = (uint)_interval;
                return currentMs + minimal;
            }
        }

        private async ValueTask FlushAsync()
        {
            List<ReadOnlyMemory<byte>> toSend = new();
            lock (_lock)
            {
                if (_updated == 0) return;

                var seg = new Segment
                {
                    Conv = _conv,
                    Cmd = IKCP_CMD_ACK,
                    Wnd = (ushort)Math.Clamp(_rcvWnd - _rcvQueue.Count, 0, 65535),
                    Una = _rcvNxt
                };

                int offset = 0;

                // 1. 发送 ACK
                for (int i = 0; i < _ackList.Count; i++)
                {
                    if (offset + IKCP_OVERHEAD > _mtu)
                    {
                        toSend.Add(_buffer.AsMemory(0, offset).ToArray());
                        offset = 0;
                    }
                    var (sn, ts) = _ackList[i];
                    seg.Sn = sn;
                    seg.Ts = ts;
                    seg.Encode(_buffer.AsSpan(offset));
                    offset += IKCP_OVERHEAD;
                }
                _ackList.Clear();

                // 2. 探测窗口大小
                if (_rmtWnd == 0)
                {
                    if (_probeWait == 0)
                    {
                        _probeWait = IKCP_PROBE_INIT;
                        _tsProbe = CurrentTimeMs() + (uint)_probeWait;
                    }
                    else
                    {
                        if (TimeDiff(CurrentTimeMs(), _tsProbe) >= 0)
                        {
                            if (_probeWait < IKCP_PROBE_INIT) _probeWait = IKCP_PROBE_INIT;
                            _probeWait += _probeWait / 2;
                            if (_probeWait > IKCP_PROBE_LIMIT) _probeWait = IKCP_PROBE_LIMIT;
                            _tsProbe = CurrentTimeMs() + (uint)_probeWait;
                            _probe |= IKCP_ASK_SEND;
                        }
                    }
                }
                else
                {
                    _tsProbe = 0;
                    _probeWait = 0;
                }

                if ((_probe & IKCP_ASK_SEND) != 0)
                {
                    seg.Cmd = IKCP_CMD_WASK;
                    if (offset + IKCP_OVERHEAD > _mtu)
                    {
                        toSend.Add(_buffer.AsMemory(0, offset).ToArray());
                        offset = 0;
                    }
                    seg.Encode(_buffer.AsSpan(offset));
                    offset += IKCP_OVERHEAD;
                }

                if ((_probe & IKCP_ASK_TELL) != 0)
                {
                    seg.Cmd = IKCP_CMD_WINS;
                    if (offset + IKCP_OVERHEAD > _mtu)
                    {
                        toSend.Add(_buffer.AsMemory(0, offset).ToArray());
                        offset = 0;
                    }
                    seg.Encode(_buffer.AsSpan(offset));
                    offset += IKCP_OVERHEAD;
                }
                _probe = 0;

                // 3. 计算发送窗口并从队列移至缓冲区
                int cwnd = Math.Min(_sndWnd, _rmtWnd);
                if (_nocwnd == 0) cwnd = Math.Min(_cwnd, cwnd);

                while (TimeDiff(_sndNxt, _sndUna + (uint)cwnd) < 0 && _sndQueue.Count > 0)
                {
                    var newSeg = _sndQueue[0];
                    _sndQueue.RemoveAt(0);

                    newSeg.Conv = _conv;
                    newSeg.Cmd = IKCP_CMD_PUSH;
                    newSeg.Wnd = seg.Wnd;
                    newSeg.Ts = CurrentTimeMs();
                    newSeg.Sn = _sndNxt++;
                    newSeg.Una = _rcvNxt;
                    newSeg.ResendTs = CurrentTimeMs();
                    newSeg.Rto = (uint)_rxRto;
                    newSeg.FastAck = 0;
                    newSeg.Xmit = 0;
                    _sndBuf.Add(newSeg);
                }

                // 4. 发送数据包
                uint current = CurrentTimeMs();
                int change = 0;
                int lost = 0;

                for (int i = 0; i < _sndBuf.Count; i++)
                {
                    var s = _sndBuf[i];
                    bool needSend = false;
                    if (s.Xmit == 0)
                    {
                        needSend = true;
                        s.Xmit++;
                        s.Rto = (uint)_rxRto;
                        s.ResendTs = current + s.Rto + (uint)(_nodelay > 0 ? 0 : (_rxRto >> 1));
                    }
                    else if (TimeDiff(current, s.ResendTs) >= 0)
                    {
                        needSend = true;
                        s.Xmit++;
                        if (_nodelay == 0)
                            s.Rto += (uint)Math.Max((int)s.Rto, _rxRto);
                        else
                        {
                            int step = (_nodelay < 2) ? (int)s.Rto : _rxRto;
                            s.Rto += (uint)(step / 2);
                        }
                        s.ResendTs = current + s.Rto;
                        lost = 1;
                    }
                    else if (s.FastAck >= (uint)_fastResend && _fastResend > 0)
                    {
                        if ((int)s.Xmit <= _deadLink || _deadLink == 0)
                        {
                            needSend = true;
                            s.Xmit++;
                            s.FastAck = 0;
                            s.ResendTs = current + s.Rto;
                            change++;
                        }
                    }

                    if (needSend)
                    {
                        s.Ts = current;
                        s.Wnd = seg.Wnd;
                        s.Una = _rcvNxt;

                        int need = IKCP_OVERHEAD + s.Len;
                        if (offset + need > _mtu)
                        {
                            toSend.Add(_buffer.AsMemory(0, offset).ToArray());
                            offset = 0;
                        }

                        s.Encode(_buffer.AsSpan(offset));
                        if (s.Len > 0 && s.Data != null)
                        {
                            s.Data.AsSpan(0, s.Len).CopyTo(_buffer.AsSpan(offset + IKCP_OVERHEAD));
                        }
                        offset += need;

                        if ((int)s.Xmit >= _deadLink && _deadLink > 0)
                        {
                            _state = -1; // dead link
                        }
                    }
                }

                if (offset > 0)
                {
                    toSend.Add(_buffer.AsMemory(0, offset).ToArray());
                }

                if (change > 0)
                {
                    int inflight = (int)(_sndNxt - _sndUna);
                    _ssthresh = (uint)Math.Max(inflight / 2, IKCP_THRESH_MIN);
                    _cwnd = (int)(_ssthresh + (uint)_fastResend);
                    _incr = _cwnd * _mss;
                }

                if (lost != 0)
                {
                    _ssthresh = (uint)Math.Max(cwnd / 2, IKCP_THRESH_MIN);
                    _cwnd = 1;
                    _incr = _mss;
                }

                if (_cwnd < 1)
                {
                    _cwnd = 1;
                    _incr = _mss;
                }
            }

            for (int i = 0; i < toSend.Count; i++)
            {
                await _output(toSend[i]);
            }
        }

        public static uint CurrentTimeMs()
        {
            return (uint)(Environment.TickCount64 & 0xFFFFFFFF);
        }
    }
}
