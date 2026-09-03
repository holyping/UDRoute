using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace UDRoute
{
    public class ServerRecordInfo
    {
        public Guid DevId { get; set; }
        public EndPoint PublicEp { get; set; } = null!;
        public string ServiceName { get; set; } = "";
        public int WanPort { get; set; }
        public bool IsTcp { get; set; }
        public KcpConfig KcpConfig { get; set; } = new();
        public int Timeout { get; set; }
        public DateTime LastSeen { get; set; }
        public bool IsAuthenticated { get; set; }
        public string OwnerUser { get; set; } = "";
        public List<IPEndPoint> LocalEps { get; set; } = new();
        public long STimestamp { get; set; }
        public long PRecvTimeTicks { get; set; }
        public bool RequiresPassword { get; set; }
    }
}
