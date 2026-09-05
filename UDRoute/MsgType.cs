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
        AuthFail = 9,
        RegFail = 10,
        AuthReq = 11,
        AuthRes = 12,
        RelayEnd = 13
    }
}