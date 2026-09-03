namespace UDRoute
{
    public class ServerRecord
    {
        public string Name { get; set; } = "";
        public string TargetIp { get; set; } = "";
        public int TargetPort { get; set; }
        public bool IsTcp { get; set; }
        public bool IsThis { get; set; }
        public string TargetServer { get; set; } = "";
        public int RegInterval { get; set; } = Constants.DefaultRegInterval;
        public int Mtu { get; set; } = Constants.DefaultMtu;
        public int Timeout { get; set; } = 0;
        public KcpConfig KcpConfig { get; set; } = new();
    }
}