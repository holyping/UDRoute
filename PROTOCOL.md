# UDRoute 专属通讯协议参考 (Proprietary Communication Protocol)

UDRoute 是一个 P2P/中继 隧道系统。该系统包含三种基本角色：
- **P端 (Proxy/Relay)**: 运行在公网的中心节点，负责信令交换、打洞协调以及流量中继。
- **S端 (Server/Target)**: 运行在 NAT 后方的服务端，提供实际的服务。
- **C端 (Client)**: 运行在 NAT 后方的客户端，请求访问 S 端的服务。

协议底层主要基于 UDP，采用自定义的二进制协议格式。当 P2P UDP 打洞成功时，C 和 S 直接通讯；否则，数据通过 P 端中继。TCP 流量通过 KCP 协议封装在 UDP 中传输。

## 1. 基础数据类型与编码 (Encoding)
所有多字节整数均采用 **Little Endian (小端序)**。
- `int32`, `uint32`, `int64`, `uint16`: 标准小端序编码。
- **String**: `4字节长度 (Int32 LE)` + `UTF-8 字节内容`。
- **EndPoint (IPEndPoint)**:
  - **IPv4**: `[Family=4 (1 byte)] + [IPv4 (4 bytes)] + [Port (4 bytes LE)]` = 9 字节
  - **IPv6**: `[Family=6 (1 byte)] + [IPv6 (16 bytes)] + [Port (4 bytes LE)]` = 21 字节
- **Guid**: 16 字节，直接使用字节序列表示。
- **KcpConfig**: 21 字节定长结构。
  - `[NoDelay (1 byte)] + [Interval (4 bytes LE)] + [Resend (4 bytes LE)] + [Nc (4 bytes LE)] + [SndWnd (4 bytes LE)] + [RcvWnd (4 bytes LE)]`

## 2. 消息类型枚举 (MsgType)
定义于 `MsgType.cs` 中。包的首字节始终是 `MsgType`。

- `Register = 1`：S -> P (注册服务)
- `Query = 2`：C -> P (查询服务 / 请求连接)
- `Punch = 3`：C <-> S (直连 P2P UDP 打洞探测 / 确认)
- `RelayStart = 4`：P -> S / C -> P (请求建立中继隧道)
- `Data = 5`：C <-> S / C <-> P <-> S (应用层载荷与多路复用)
- `Disconnect = 6`：断开会话
- `EchoReq = 7`：S/C -> P (STUN 探测，获取公网 IP)
- `EchoResp = 8`：P -> S/C (STUN 响应)
- `RegisterAck = 9`：P -> S (注册响应结果)
- `AuthReq = 11`：C -> S (客户端向服务端发起鉴权/挑战请求)
- `AuthRes = 12`：S -> C (鉴权结果响应)
- `RelayEnd = 13`：C/S -> P (打洞成功后，通知 P 端释放中继资源)
- `RelayStartAck = 14`：S -> P (响应中继建立请求)

## 3. 核心数据包结构解析 (Packet Formats)

### 3.1. 服务注册阶段 (S <-> P)
**Register (1)**
- `[MsgType = 1] (1 byte)`
- `[ContextId] (2 bytes)`
- `[DevId] (16 bytes, Guid)`
- `[WanPort] (4 bytes)`
- `[IsTcp] (1 byte)`
- `[Timeout] (4 bytes)`
- `[Timestamp] (8 bytes)`
- `[ReqPass] (1 byte)`
- `[KcpConfig] (21 bytes)`
- `[Name] (String)`
- `[DevName] (String)`
- `[EpCount] (1 byte)`
- `[LocalEps...] (可变长度，包含 EpCount 个 EndPoint)`
- `[Username] (String)`
- `[PasswordPayload] (String)`
- `[TunnelReuseInterval] (4 bytes)`
- `[AllowRelay] (1 byte)`

**RegisterAck (9)**
- `[MsgType = 9] (1 byte)`
- `[ContextId] (2 bytes)`
- `[Status] (1 byte)` (1 = 成功，0 = 失败)
- `[Reason] (String, 可选)`

### 3.2. 服务查询与中继初始化 (C <-> P <-> S)
**Query (2)** (C 发送给 P)
- `[MsgType = 2] (1 byte)`
- `[ContextId] (2 bytes)`
- `[SessionId] (16 bytes, Guid)`
- `[TargetName] (String)`
- `[Flags] (1 byte)` (Bit0: ForceRelay, Bit1: IsReuse)

**Query Response (隐含，复用相关结构或作为 P 对 C 的直接回复)**
- 如果成功 (Status = 1): 返回 S 端的公网/内网地址、鉴权要求等信息。
- 如果失败 (Status != 1): 返回失败原因。(`PunchStatus.NotFound`, `SUnresponsive` 等)

**RelayStart (4)** (请求开启中继)
- `[MsgType = 4] (1 byte)`
- `[ContextId] (2 bytes)`
- `[SessionId] (16 bytes)`
- `[TargetName] (String)`
- `[ClientPublicEp] (EndPoint)`
- `[Flags] (1 byte)` (Bit0: AllowRelay, Bit1: ClientForceRelay, Bit2: IsReuse)

**RelayStartAck (14)**
- `[MsgType = 14] (1 byte)`
- `[ContextId] (2 bytes)`
- `[SessionId] (16 bytes)`
- `[Status] (1 byte)`

### 3.3. NAT 穿透/打洞 (Hole Punching)
**EchoReq (7) / EchoResp (8)** (简单的 STUN 机制)
- Request: `[MsgType = 7 (1 byte)] + [EchoId/SessionId (16 bytes)]`
- Response: `[MsgType = 8 (1 byte)] + [EchoId (16 bytes)] + [PublicEp (EndPoint)]`

**Punch (3)**
- `[MsgType = 3] (1 byte)`
- `[ContextId] (2 bytes)` (ACK时常为0)
- `[SessionId] (16 bytes)`
- `[DevId] (16 bytes)`
- `[Status] (1 byte)` (2 = 打洞探测 PunchReq, 3 = 打洞确认 PunchAck)

### 3.4. 鉴权握手 (AuthReq / AuthRes)
**AuthReq (11)** (C -> S 挑战与哈希验证)
- `[MsgType = 11] (1 byte)`
- `[SessionId] (16 bytes)`
- `[AuthTimestamp] (8 bytes LE)`
- `[AuthHash] (32 bytes，基于密码和服务器挑战盐值的 SHA256 哈希)`

**AuthRes (12)** (S -> C)
- `[MsgType = 12] (1 byte)`
- `[SessionId] (16 bytes)`
- `[Status] (1 byte)` (1 = 成功，0 = 失败)
- `[ServerT1] (String, 可选)` (如果初次探测没有挑战盐值，则 S 端返回生成的 T1 挑战码供客户端重新哈希)

### 3.5. 数据传输与多路复用 (Data & Mux)
隧道建立后，实际的应用流量均使用 **Data (5)** 封包传输。

**Data 包基础头部:**
- `[MsgType = 5] (1 byte)`
- `[SessionId] (16 bytes)`
- `[MuxType] (1 byte)`

**多路复用类型 (MuxType):**
1. **UDP (MuxType = 1)**：用于代理 UDP 应用层流量。
   - 结构: `[ChannelId (4 bytes LE)] + [ChannelCmd (1 byte)] + [UDP 载荷]`
   - ChannelCmd: 1=Open, 2=Data, 3=Close, 4=KeepAlive
2. **KCP (MuxType = 2)**：用于代理 TCP 应用层流量 (在 UDP 上模拟可靠传输)。
   - 结构: `[KCP 标准头部及封包数据]`
   - 在 KCP 解包后，内部再次进行 Channel 分发 (`[ChannelId (4 bytes LE)] + [Cmd (1 byte)] + [PayloadLen (2 bytes LE)] + [Payload]`)。
3. **KeepAlive (MuxType = 3)**：隧道级心跳保活。
   - 用于维持 NAT 路由器上的 UDP 孔洞绑定关系，无附加载荷。

### 3.6. 拆除与清理
- **Disconnect (6)**: 携带 `[SessionId]` 即可，用于终止整个会话。
- **RelayEnd (13)**: 携带 `[SessionId]`。当 C 与 S 打洞成功并建立直连通讯后，发送给 P，告知 P 端不再需要中继，释放服务器内存资源。

