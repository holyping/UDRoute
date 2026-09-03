namespace UDRoute
{
    // ==========================================
    // 9. 配置解析 (无反射，纯字符串处理)
    // ==========================================
    public class AppConfig
    {
        public int Port { get; set; } // 默认0。若确定启用P模式且Port仍为0，则由解析器设置为Constants.DefaultProxyPort
        public int WanPort { get; set; } // 默认0表示未配置外网NAT端口映射，仅在显式配置时生效
        public int RegTimeout { get; set; } = Constants.DefaultRegTimeout;
        public string DevName { get; set; } = Environment.MachineName;
        public Guid DevId { get; set; }

        public bool EnableProxy { get; set; }

        public Logging.LogLevel LogLevel { get; set; } = Program.IsServiceMode ? Logging.LogLevel.Warn : Logging.LogLevel.Info;
        public Logging.LogType LogType { get; set; } = Logging.LogType.Default;
        public string LogFile { get; set; } = "";
        
        public string ConfigPath { get; set; } = "";
        public string AuthFile { get; set; } = "";
        public string AuthMode { get; set; } = "none"; // none, strict, optional
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        public List<ClientRecord> ClientRecords { get; set; } = new();
        public List<ServerRecord> ServerRecords { get; set; } = new();
    }
}