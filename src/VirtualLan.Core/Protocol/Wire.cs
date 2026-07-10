using System.Buffers.Binary;
using System.Net;
using System.Text;
using VirtualLan.Core.Crypto;
using VirtualLan.Core.Net;

namespace VirtualLan.Core.Protocol;

/// <summary>
/// Serialização de todos os pacotes de controle. Os pacotes de dados são montados
/// aqui só até o AAD; o corpo é selado por <see cref="FrameCipher"/>.
///
/// Convenção: todos os inteiros multibyte são big-endian (network byte order).
/// </summary>
public static class Wire
{
    /// <summary>Maior datagrama que emitimos ou aceitamos. Um datagrama maior é descartado sem parse.</summary>
    public const int MaxPacketSize = 1600;

    /// <summary>Offset do corpo selado em <see cref="PacketType.DataDirect"/>: header + srcNodeId.</summary>
    public const int DataDirectPayloadOffset = PacketHeader.Size + NodeId.Size; // 24

    /// <summary>Offset do corpo selado em <see cref="PacketType.DataRelay"/>: header + networkId + src + dst.</summary>
    public const int DataRelayPayloadOffset = PacketHeader.Size + NetworkId.Size + NodeId.Size + NodeId.Size; // 56

    /// <summary>Bytes de overhead do caminho mais caro (relay), sem contar IP/UDP.</summary>
    public const int MaxAppOverhead = DataRelayPayloadOffset + FrameCipher.Overhead; // 84

    /// <summary>Maior quadro Ethernet que cabe em <see cref="MaxPacketSize"/>.</summary>
    public const int MaxFrameSize = MaxPacketSize - MaxAppOverhead; // 1516

    private const int MaxLocalEndpoints = 8;

    // ----------------------------------------------------------------------------- Register

    public static int WriteRegister(
        Span<byte> buffer,
        NetworkId networkId,
        NodeId nodeId,
        MacAddress mac,
        IReadOnlyList<IPEndPoint> localEndpoints)
    {
        PacketHeader.Write(buffer, PacketType.Register);

        var w = new SpanWriter(buffer[PacketHeader.Size..]);
        w.WriteNetworkId(networkId);
        w.WriteNodeId(nodeId);
        w.WriteMac(mac);

        int count = Math.Min(localEndpoints.Count, MaxLocalEndpoints);
        w.WriteByte((byte)count);
        for (int i = 0; i < count; i++) w.WriteEndpoint(localEndpoints[i]);

        return PacketHeader.Size + w.Position;
    }

    public static bool TryReadRegister(
        ReadOnlySpan<byte> body,
        out NetworkId networkId,
        out NodeId nodeId,
        out MacAddress mac,
        out List<IPEndPoint> localEndpoints)
    {
        networkId = default; nodeId = default; mac = default;
        localEndpoints = [];

        var r = new SpanReader(body);
        if (!r.TryReadNetworkId(out networkId)) return false;
        if (!r.TryReadNodeId(out nodeId)) return false;
        if (!r.TryReadMac(out mac)) return false;
        if (!r.TryReadByte(out byte count)) return false;
        if (count > MaxLocalEndpoints) return false;

        for (int i = 0; i < count; i++)
        {
            if (!r.TryReadEndpoint(out var ep)) return false;
            if (ep is not null) localEndpoints.Add(ep);
        }

        return true;
    }

    // ------------------------------------------------------------------ RegisterAck / PeerUpdate

    public static int WriteRegisterAck(Span<byte> buffer, byte assignedIndex, IReadOnlyList<PeerRecord> peers)
    {
        PacketHeader.Write(buffer, PacketType.RegisterAck);

        var w = new SpanWriter(buffer[PacketHeader.Size..]);
        w.WriteByte(assignedIndex);
        WritePeerList(ref w, peers);

        return PacketHeader.Size + w.Position;
    }

    public static bool TryReadRegisterAck(ReadOnlySpan<byte> body, out byte assignedIndex, out List<PeerRecord> peers)
    {
        assignedIndex = 0;
        peers = [];

        var r = new SpanReader(body);
        if (!r.TryReadByte(out assignedIndex)) return false;
        return TryReadPeerList(ref r, peers);
    }

    public static int WritePeerUpdate(Span<byte> buffer, IReadOnlyList<PeerRecord> peers)
    {
        PacketHeader.Write(buffer, PacketType.PeerUpdate);

        var w = new SpanWriter(buffer[PacketHeader.Size..]);
        WritePeerList(ref w, peers);

        return PacketHeader.Size + w.Position;
    }

    public static bool TryReadPeerUpdate(ReadOnlySpan<byte> body, out List<PeerRecord> peers)
    {
        peers = [];
        var r = new SpanReader(body);
        return TryReadPeerList(ref r, peers);
    }

    private static void WritePeerList(ref SpanWriter w, IReadOnlyList<PeerRecord> peers)
    {
        if (peers.Count > 254) throw new ArgumentException("Máximo de 254 peers por rede.", nameof(peers));

        w.WriteByte((byte)peers.Count);
        for (int i = 0; i < peers.Count; i++) peers[i].WriteTo(ref w);
    }

    private static bool TryReadPeerList(ref SpanReader r, List<PeerRecord> peers)
    {
        if (!r.TryReadByte(out byte count)) return false;

        peers.Capacity = count;
        for (int i = 0; i < count; i++)
        {
            if (!PeerRecord.TryRead(ref r, out var record)) return false;
            peers.Add(record);
        }

        return true;
    }

    // ------------------------------------------------------------------ Keepalive / Disconnect

    public static int WriteKeepalive(Span<byte> buffer, NetworkId networkId, NodeId nodeId)
        => WriteNetworkAndNode(buffer, PacketType.Keepalive, networkId, nodeId);

    public static int WriteDisconnect(Span<byte> buffer, NetworkId networkId, NodeId nodeId)
        => WriteNetworkAndNode(buffer, PacketType.Disconnect, networkId, nodeId);

    private static int WriteNetworkAndNode(Span<byte> buffer, PacketType type, NetworkId networkId, NodeId nodeId)
    {
        PacketHeader.Write(buffer, type);

        var w = new SpanWriter(buffer[PacketHeader.Size..]);
        w.WriteNetworkId(networkId);
        w.WriteNodeId(nodeId);

        return PacketHeader.Size + w.Position;
    }

    /// <summary>Vale tanto para <see cref="PacketType.Keepalive"/> quanto <see cref="PacketType.Disconnect"/>.</summary>
    public static bool TryReadNetworkAndNode(ReadOnlySpan<byte> body, out NetworkId networkId, out NodeId nodeId)
    {
        networkId = default; nodeId = default;

        var r = new SpanReader(body);
        return r.TryReadNetworkId(out networkId) && r.TryReadNodeId(out nodeId);
    }

    // ----------------------------------------------------------------------- Punch / PunchAck

    public static int WritePunch(Span<byte> buffer, NodeId nodeId, uint nonce)
        => WritePunchLike(buffer, PacketType.Punch, nodeId, nonce);

    public static int WritePunchAck(Span<byte> buffer, NodeId nodeId, uint nonce)
        => WritePunchLike(buffer, PacketType.PunchAck, nodeId, nonce);

    private static int WritePunchLike(Span<byte> buffer, PacketType type, NodeId nodeId, uint nonce)
    {
        PacketHeader.Write(buffer, type);
        nodeId.WriteTo(buffer.Slice(PacketHeader.Size, NodeId.Size));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(PacketHeader.Size + NodeId.Size, 4), nonce);

        return PacketHeader.Size + NodeId.Size + 4;
    }

    public static bool TryReadPunch(ReadOnlySpan<byte> body, out NodeId nodeId, out uint nonce)
    {
        nodeId = default; nonce = 0;

        var r = new SpanReader(body);
        if (!r.TryReadNodeId(out nodeId)) return false;
        if (!r.TryReadBytes(4, out var n)) return false;

        nonce = BinaryPrimitives.ReadUInt32BigEndian(n);
        return true;
    }

    // ------------------------------------------------------------------------------- Data

    /// <summary>
    /// Escreve o prefixo em claro de um <see cref="PacketType.DataDirect"/>.
    /// Esse prefixo é exatamente o AAD; o quadro deve ser selado a partir de
    /// <see cref="DataDirectPayloadOffset"/>.
    /// </summary>
    public static void WriteDataDirectHeader(Span<byte> buffer, NodeId sourceNodeId)
    {
        PacketHeader.Write(buffer, PacketType.DataDirect);
        sourceNodeId.WriteTo(buffer.Slice(PacketHeader.Size, NodeId.Size));
    }

    public static bool TryReadDataDirectHeader(ReadOnlySpan<byte> packet, out NodeId sourceNodeId)
    {
        sourceNodeId = default;
        if (packet.Length < DataDirectPayloadOffset + FrameCipher.Overhead) return false;

        sourceNodeId = new NodeId(packet.Slice(PacketHeader.Size, NodeId.Size));
        return true;
    }

    /// <summary>
    /// Escreve o prefixo em claro de um <see cref="PacketType.DataRelay"/>.
    /// <paramref name="destinationNodeId"/> igual a <see cref="NodeId.Zero"/> significa
    /// broadcast para toda a rede.
    /// </summary>
    public static void WriteDataRelayHeader(
        Span<byte> buffer, NetworkId networkId, NodeId sourceNodeId, NodeId destinationNodeId)
    {
        PacketHeader.Write(buffer, PacketType.DataRelay);

        var w = new SpanWriter(buffer[PacketHeader.Size..]);
        w.WriteNetworkId(networkId);
        w.WriteNodeId(sourceNodeId);
        w.WriteNodeId(destinationNodeId);
    }

    public static bool TryReadDataRelayHeader(
        ReadOnlySpan<byte> packet, out NetworkId networkId, out NodeId sourceNodeId, out NodeId destinationNodeId)
    {
        networkId = default; sourceNodeId = default; destinationNodeId = default;
        if (packet.Length < DataRelayPayloadOffset + FrameCipher.Overhead) return false;

        var r = new SpanReader(packet[PacketHeader.Size..]);
        return r.TryReadNetworkId(out networkId)
            && r.TryReadNodeId(out sourceNodeId)
            && r.TryReadNodeId(out destinationNodeId);
    }

    // ------------------------------------------------------------------------------ Error

    public static int WriteError(Span<byte> buffer, ErrorCode code, string message)
    {
        PacketHeader.Write(buffer, PacketType.Error);
        buffer[PacketHeader.Size] = (byte)code;

        int offset = PacketHeader.Size + 1;

        // Trunca no limite de bytes disponíveis sem partir um code point no meio.
        byte[] utf8 = Encoding.UTF8.GetBytes(message);
        int room = Math.Min(buffer.Length - offset, 256);
        int take = utf8.Length;

        if (take > room)
        {
            take = room;
            while (take > 0 && (utf8[take - 1] & 0xC0) == 0x80) take--; // recua sobre continuações
            if (take > 0 && utf8[take - 1] >= 0xC0) take--;             // descarta lead byte órfão
        }

        utf8.AsSpan(0, take).CopyTo(buffer[offset..]);
        return offset + take;
    }

    public static bool TryReadError(ReadOnlySpan<byte> body, out ErrorCode code, out string message)
    {
        code = ErrorCode.Unknown;
        message = string.Empty;

        if (body.Length < 1) return false;

        code = (ErrorCode)body[0];
        message = Encoding.UTF8.GetString(body[1..]);
        return true;
    }

    /// <summary>Corpo do pacote, isto é, tudo depois do cabeçalho de 8 bytes.</summary>
    public static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> packet) => packet[PacketHeader.Size..];
}
