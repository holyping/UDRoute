using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UDRoute;
using Xunit;

namespace UDRoute.Tests;

public class KcpTests
{
    private static byte[] CreateKcpPushPacket(uint conv, uint sn, uint una, byte[] data)
    {
        byte[] packet = new byte[Kcp.IKCP_OVERHEAD + data.Length];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), conv);
        span[4] = Kcp.IKCP_CMD_PUSH;
        span[5] = 0; // frg
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), 512); // wnd
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), 100); // ts
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), sn); // sn
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), una); // una
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20, 4), data.Length); // len
        data.CopyTo(span.Slice(24));
        return packet;
    }

    [Fact]
    public void OutOfOrderPackets_MustBeCorrectlySortedAndDelivered()
    {
        uint conv = 0x12345678;
        var kcp = new Kcp(conv, _ => ValueTask.CompletedTask);
        kcp.SetWindowSize(128, 128);

        // Packets: sn=0, sn=1, sn=3, sn=2, sn=4
        byte[] p0 = CreateKcpPushPacket(conv, 0, 0, new byte[] { 100 });
        byte[] p1 = CreateKcpPushPacket(conv, 1, 0, new byte[] { 101 });
        byte[] p2 = CreateKcpPushPacket(conv, 2, 0, new byte[] { 102 });
        byte[] p3 = CreateKcpPushPacket(conv, 3, 0, new byte[] { 103 });
        byte[] p4 = CreateKcpPushPacket(conv, 4, 0, new byte[] { 104 });

        // Feed in order: 0, 1
        Assert.Equal(0, kcp.Input(p0));
        Assert.Equal(0, kcp.Input(p1));

        // Feed out of order: 3 arrived before 2!
        Assert.Equal(0, kcp.Input(p3));
        Assert.Equal(0, kcp.Input(p2));

        // Feed 4
        Assert.Equal(0, kcp.Input(p4));

        // Now drain kcp.Recv
        byte[] buf = new byte[1024];
        var received = new List<byte>();
        while (true)
        {
            int r = kcp.Recv(buf);
            if (r <= 0) break;
            for (int i = 0; i < r; i++) received.Add(buf[i]);
        }

        // All 5 packets must be received in sequential order: 100, 101, 102, 103, 104
        Assert.Equal(new byte[] { 100, 101, 102, 103, 104 }, received);
    }

    [Fact]
    public void MassiveReordering_RandomOrder_ReassemblesCorrectly()
    {
        uint conv = 0x87654321;
        var kcp = new Kcp(conv, _ => ValueTask.CompletedTask);
        kcp.SetWindowSize(256, 256);

        const int count = 50;
        var packets = new List<(uint sn, byte[] data)>();
        for (uint i = 0; i < count; i++)
        {
            packets.Add((i, CreateKcpPushPacket(conv, i, 0, new byte[] { (byte)i })));
        }

        // Shuffle with seed
        var rng = new Random(42);
        var shuffled = packets.OrderBy(_ => rng.Next()).ToList();

        foreach (var p in shuffled)
        {
            Assert.Equal(0, kcp.Input(p.data));
        }

        byte[] buf = new byte[1024];
        var received = new List<byte>();
        while (true)
        {
            int r = kcp.Recv(buf);
            if (r <= 0) break;
            for (int i = 0; i < r; i++) received.Add(buf[i]);
        }

        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((byte)i, received[i]);
        }
    }
}
