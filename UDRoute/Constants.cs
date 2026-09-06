namespace UDRoute
{
    public static class Constants
    {
        public const int DefaultProxyPort = 9400;
        public const int DefaultMtu = 1400;
        public const int DefaultRegInterval = 300;
        public const int DefaultRegTimeout = 600;
        public const int DefaultTimeout = 300;
        public const int DefaultTunnelReuseInterval = 300;
        public const int DefaultMaxUnauthNamesPerUser = 20;
        public const int DefaultMaxUnauthNamesTotal = 2000;
        public const int DefaultIdleThreshold = 60;
        public const int DefaultProbeTimeout = 5;
        public const int DefaultMaxRecentRequests = 100;
        public const int MinMaxRecentRequests = 50;
        public const int DefaultMaxSize = DefaultMaxRecentRequests;
        public const int MinMaxSize = MinMaxRecentRequests;
        public const int DefaultRetryIntervalMs = 500;
    }
}

