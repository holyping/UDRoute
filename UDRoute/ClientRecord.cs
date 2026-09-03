namespace UDRoute
{
    public class ClientRecord
    {
        public int Port { get; set; }
        public bool IsTcp { get; set; }
        public bool IsThis { get; set; }
        public string TargetName { get; set; } = "";
        public string TargetServer { get; set; } = "";
        public string? Username { get; set; }
        public byte[]? Password { get; set; }
        public int Mtu { get; set; } = Constants.DefaultMtu;
    }
}