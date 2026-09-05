namespace UDRoute
{
    public class ServerRecord
    {
        public string Name { get; set; } = "";
        public string TargetIp { get; set; } = "";
        public int TargetPort { get; set; }
        public bool IsTcp { get; set; }
        public bool IsFile { get; set; }
        public string BaseDir { get; set; } = "";
        public bool ReadOnly { get; set; }
        public bool IsThis { get; set; }
        public string TargetServer { get; set; } = "";
        public int RegInterval { get; set; } = Constants.DefaultRegInterval;
        public int Mtu { get; set; } = Constants.DefaultMtu;
        public int Timeout { get; set; } = 0;
        public int TunnelReuseInterval { get; set; } = Constants.DefaultTunnelReuseInterval;
        public bool AllowRelay { get; set; } = true;
        public string? Username { get; set; }
        public byte[]? Password { get; set; }
        public KcpConfig KcpConfig { get; set; } = new();
    }
}