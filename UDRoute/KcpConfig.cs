namespace UDRoute
{
    public class KcpConfig
    {
        public bool NoDelay { get; set; } = false; // 默认关闭，减少小包与无延迟ACK的发包频率
        public int Interval { get; set; } = 40; // 调大 Interval，减少 KCP 内部定时器轮询和心跳频率
        public int Resend { get; set; } = 2; // 2 = 开启快速重传(跳过2个ACK重传)。类似于标准TCP的机制，兼顾流畅度
        public int Nc { get; set; } = 0; // 0 = 开启拥塞控制，防止拥堵时疯狂发包触发 UDP Flood 防御机制
        public int SndWnd { get; set; } = 512; // 开启拥塞控制后，可适当调大窗口上限以利用高质量宽带
        public int RcvWnd { get; set; } = 512;

        public KcpConfig Clone() => new KcpConfig
        {
            NoDelay = this.NoDelay,
            Interval = this.Interval,
            Resend = this.Resend,
            Nc = this.Nc,
            SndWnd = this.SndWnd,
            RcvWnd = this.RcvWnd
        };

        public void SetProfile(string profile)
        {
            if (profile.Equals("normal", StringComparison.OrdinalIgnoreCase))
            {
                NoDelay = false;
                Interval = 40;
                Resend = 2;
                Nc = 0;
                SndWnd = 512;
                RcvWnd = 512;
            }
            else if (profile.Equals("fast", StringComparison.OrdinalIgnoreCase))
            {
                NoDelay = true;
                Interval = 20;
                Resend = 2;
                Nc = 1;
                SndWnd = 256;
                RcvWnd = 256;
            }
            else if (profile.Equals("api", StringComparison.OrdinalIgnoreCase))
            {
                NoDelay = true;
                Interval = 10;
                Resend = 0;
                Nc = 1;
                SndWnd = 128;
                RcvWnd = 128;
            }
        }
    }
}

