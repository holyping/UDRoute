namespace UDRoute
{
    // ==========================================
    // 4. 二进制协议定义 (Protocol)
    // ==========================================
    public enum MsgType : byte
    {
        Register = 1,
        Query = 2,
        Punch = 3,
        RelayStart = 4,
        Data = 5,
        Disconnect = 6,
        EchoReq = 7,
        EchoResp = 8,
        RegisterAck = 9,
        AuthReq = 11,
        AuthRes = 12,
        RelayEnd = 13,
        RelayStartAck = 14
    }

    public static class PunchStatus
    {
        public const byte NotFound = 0;       // 服务未在 P 端注册 (Target not registered)
        public const byte Success = 1;        // 查询成功 (Query success)
        public const byte PunchReq = 2;       // P2P 直连打洞探测 (Direct punch request)
        public const byte PunchAck = 3;       // P2P 直连打洞确认 (Punch ACK)
        public const byte SUnresponsive = 4;  // 通道健康探测超时 / S 端无响应 / 保活失效 (Health probe timed out / KeepAlive failed)
        public const byte StaleSession = 5;   // S 端在会话建立后已重启，原会话失效 (S restarted since session creation)
    }
}