using System.Net;
using VirtualLan.Core.Crypto;
using VirtualLan.Core.Net;
using VirtualLan.Core.Protocol;
using Xunit;

namespace VirtualLan.Core.Tests;

public class WireTests
{
    private static readonly NetworkId SampleNetworkId = NetworkKeys.Derive("teste", "senha").NetworkId;

    [Fact]
    public void Header_RoundTrips()
    {
        Span<byte> buffer = stackalloc byte[PacketHeader.Size];
        PacketHeader.Write(buffer, PacketType.DataDirect);

        Assert.True(PacketHeader.TryRead(buffer, out var type));
        Assert.Equal(PacketType.DataDirect, type);
    }

    [Fact]
    public void Header_RejectsWrongMagic()
    {
        Span<byte> buffer = stackalloc byte[PacketHeader.Size];
        PacketHeader.Write(buffer, PacketType.Keepalive);
        buffer[0] = (byte)'X';

        Assert.False(PacketHeader.TryRead(buffer, out _));
    }

    [Fact]
    public void Header_RejectsWrongVersion()
    {
        Span<byte> buffer = stackalloc byte[PacketHeader.Size];
        PacketHeader.Write(buffer, PacketType.Keepalive);
        buffer[4] = 99;

        Assert.False(PacketHeader.TryRead(buffer, out _));
        Assert.True(PacketHeader.TryPeekVersion(buffer, out byte version));
        Assert.Equal(99, version);
    }

    [Fact]
    public void Register_RoundTrips()
    {
        var nodeId = NodeId.CreateRandom();
        var mac = MacAddress.Parse("02:ab:cd:ef:00:11");
        List<IPEndPoint> local = [new(IPAddress.Parse("192.168.0.10"), 51820), new(IPAddress.Parse("10.0.0.5"), 51820)];

        byte[] buffer = new byte[Wire.MaxPacketSize];
        int length = Wire.WriteRegister(buffer, SampleNetworkId, nodeId, mac, local);

        Assert.True(PacketHeader.TryRead(buffer.AsSpan(0, length), out var type));
        Assert.Equal(PacketType.Register, type);

        Assert.True(Wire.TryReadRegister(
            Wire.Body(buffer.AsSpan(0, length)), out var netId, out var readNodeId, out var readMac, out var readLocal));

        Assert.Equal(SampleNetworkId, netId);
        Assert.Equal(nodeId, readNodeId);
        Assert.Equal(mac, readMac);
        Assert.Equal(local, readLocal);
    }

    [Fact]
    public void RegisterAck_RoundTripsPeerList()
    {
        List<PeerRecord> peers =
        [
            new(NodeId.CreateRandom(), MacAddress.CreateRandomLocal(), 7,
                new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000),
                new IPEndPoint(IPAddress.Parse("192.168.1.20"), 51820)),
            new(NodeId.CreateRandom(), MacAddress.CreateRandomLocal(), 12,
                new IPEndPoint(IPAddress.Parse("198.51.100.4"), 41000),
                null),
        ];

        byte[] buffer = new byte[Wire.MaxPacketSize];
        int length = Wire.WriteRegisterAck(buffer, assignedIndex: 3, peers);

        Assert.True(Wire.TryReadRegisterAck(Wire.Body(buffer.AsSpan(0, length)), out byte index, out var readPeers));
        Assert.Equal(3, index);
        Assert.Equal(peers, readPeers);
        Assert.Null(readPeers[1].LocalEndpoint);
    }

    [Fact]
    public void Punch_RoundTrips()
    {
        var nodeId = NodeId.CreateRandom();

        byte[] buffer = new byte[64];
        int length = Wire.WritePunchAck(buffer, nodeId, 0xDEADBEEF);

        Assert.True(PacketHeader.TryRead(buffer.AsSpan(0, length), out var type));
        Assert.Equal(PacketType.PunchAck, type);

        Assert.True(Wire.TryReadPunch(Wire.Body(buffer.AsSpan(0, length)), out var readNodeId, out uint nonce));
        Assert.Equal(nodeId, readNodeId);
        Assert.Equal(0xDEADBEEFu, nonce);
    }

    [Fact]
    public void Error_TruncatesWithoutSplittingCodePoints()
    {
        byte[] buffer = new byte[PacketHeader.Size + 1 + 5]; // só 5 bytes para a mensagem
        int length = Wire.WriteError(buffer, ErrorCode.NetworkFull, "ãããããããã"); // 2 bytes por char

        Assert.True(Wire.TryReadError(Wire.Body(buffer.AsSpan(0, length)), out var code, out string message));
        Assert.Equal(ErrorCode.NetworkFull, code);
        Assert.Equal("ãã", message); // 4 bytes; o 5º seria metade de um code point
    }

    [Fact]
    public void DataRelayHeader_RoundTripsAndRejectsShortPackets()
    {
        var src = NodeId.CreateRandom();
        var dst = NodeId.CreateRandom();

        byte[] buffer = new byte[Wire.MaxPacketSize];
        Wire.WriteDataRelayHeader(buffer, SampleNetworkId, src, dst);

        // Sem corpo selado, o pacote é curto demais e deve ser rejeitado.
        Assert.False(Wire.TryReadDataRelayHeader(buffer.AsSpan(0, Wire.DataRelayPayloadOffset), out _, out _, out _));

        int fullLength = Wire.DataRelayPayloadOffset + FrameCipher.Overhead;
        Assert.True(Wire.TryReadDataRelayHeader(buffer.AsSpan(0, fullLength), out var netId, out var readSrc, out var readDst));

        Assert.Equal(SampleNetworkId, netId);
        Assert.Equal(src, readSrc);
        Assert.Equal(dst, readDst);
    }

    [Fact]
    public void MaxFrame_FitsInMaxPacket()
    {
        Assert.True(Wire.MaxFrameSize >= EthernetFrame.MaxSize);
        Assert.True(Wire.DataRelayPayloadOffset + FrameCipher.Overhead + EthernetFrame.MaxSize <= Wire.MaxPacketSize);
    }
}
