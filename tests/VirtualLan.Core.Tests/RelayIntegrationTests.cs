using System.Net;
using System.Net.Sockets;
using VirtualLan.Core.Crypto;
using VirtualLan.Core.Net;
using VirtualLan.Core.Protocol;
using VirtualLan.Relay;
using Xunit;

namespace VirtualLan.Core.Tests;

/// <summary>
/// Sobe um <see cref="RelayServer"/> real em loopback e conversa com ele por UDP, como um nó faria.
/// Cobre o caminho que não dá para testar em unidade: registro, atribuição de índice,
/// divulgação de peers e roteamento de quadros cifrados.
/// </summary>
public class RelayIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TwoNodes_RegisterAndExchangeEncryptedFrames()
    {
        int port = FindFreeUdpPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var relay = new RelayServer(port);
        var relayTask = relay.RunAsync(cts.Token);

        var relayEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        var keys = NetworkKeys.Derive("integracao", "senha-de-teste");

        using var alice = new FakeNode(keys);
        using var bob = new FakeNode(keys);

        // --- registro ---
        byte aliceIndex = await alice.RegisterAsync(relayEndpoint);
        Assert.Equal(1, aliceIndex);

        byte bobIndex = await bob.RegisterAsync(relayEndpoint);
        Assert.Equal(2, bobIndex);

        // Alice recebe um PeerUpdate anunciando Bob.
        var update = await alice.ReceiveAsync(PacketType.PeerUpdate);
        Assert.True(Wire.TryReadPeerUpdate(Wire.Body(update.Span), out var peers));
        Assert.Single(peers);
        Assert.Equal(bob.NodeId, peers[0].NodeId);
        Assert.Equal(bob.Mac, peers[0].Mac);
        Assert.Equal(2, peers[0].Index);

        // --- dados: Alice → relay → Bob ---
        byte[] frame = BuildBroadcastFrame(alice.Mac, payload: "sala criada"u8);
        await alice.SendDataRelayAsync(relayEndpoint, bob.NodeId, frame);

        var relayed = await bob.ReceiveAsync(PacketType.DataRelay);

        Assert.True(Wire.TryReadDataRelayHeader(relayed.Span, out var networkId, out var srcNodeId, out var dstNodeId));
        Assert.Equal(keys.NetworkId, networkId);
        Assert.Equal(alice.NodeId, srcNodeId);
        Assert.Equal(bob.NodeId, dstNodeId);

        byte[] recovered = new byte[Wire.MaxFrameSize];
        Assert.True(bob.Cipher.TryOpen(relayed.Span, Wire.DataRelayPayloadOffset, recovered, out int length));
        Assert.Equal(frame, recovered[..length]);

        await cts.CancelAsync();
        await relayTask.WaitAsync(Timeout); // desligamento limpo: RunAsync não propaga o cancelamento
    }

    [Fact]
    public async Task BroadcastToZero_ReachesEveryOtherNode()
    {
        int port = FindFreeUdpPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var relay = new RelayServer(port);
        var relayTask = relay.RunAsync(cts.Token);

        var relayEndpoint = new IPEndPoint(IPAddress.Loopback, port);
        var keys = NetworkKeys.Derive("broadcast", "senha-de-teste");

        using var host = new FakeNode(keys);
        using var guest1 = new FakeNode(keys);
        using var guest2 = new FakeNode(keys);

        await host.RegisterAsync(relayEndpoint);
        await guest1.RegisterAsync(relayEndpoint);
        await guest2.RegisterAsync(relayEndpoint);

        byte[] frame = BuildBroadcastFrame(host.Mac, "descoberta lan"u8);
        await host.SendDataRelayAsync(relayEndpoint, NodeId.Zero, frame);

        // Ambos os convidados recebem — é isto que faz a sala aparecer na lista do jogo.
        foreach (var guest in new[] { guest1, guest2 })
        {
            var packet = await guest.ReceiveAsync(PacketType.DataRelay);

            byte[] recovered = new byte[Wire.MaxFrameSize];
            Assert.True(guest.Cipher.TryOpen(packet.Span, Wire.DataRelayPayloadOffset, recovered, out int length));
            Assert.Equal(frame, recovered[..length]);
        }

        await cts.CancelAsync();
        await relayTask.WaitAsync(Timeout); // desligamento limpo: RunAsync não propaga o cancelamento
    }

    [Fact]
    public async Task NodeOfAnotherNetwork_IsNeverRoutedTo()
    {
        int port = FindFreeUdpPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var relay = new RelayServer(port);
        var relayTask = relay.RunAsync(cts.Token);

        var relayEndpoint = new IPEndPoint(IPAddress.Loopback, port);

        using var alice = new FakeNode(NetworkKeys.Derive("rede-a", "senha-a"));
        using var mallory = new FakeNode(NetworkKeys.Derive("rede-b", "senha-b"));

        await alice.RegisterAsync(relayEndpoint);
        await mallory.RegisterAsync(relayEndpoint);

        // Ambas as redes atribuem índice 1: são espaços de endereçamento independentes.
        byte[] frame = BuildBroadcastFrame(alice.Mac, "segredo"u8);
        await alice.SendDataRelayAsync(relayEndpoint, NodeId.Zero, frame);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => mallory.ReceiveAsync(PacketType.DataRelay, TimeSpan.FromMilliseconds(500)).AsTask());

        await cts.CancelAsync();
        await relayTask.WaitAsync(Timeout); // desligamento limpo: RunAsync não propaga o cancelamento
    }

    // ------------------------------------------------------------------------------ helpers

    private static byte[] BuildBroadcastFrame(MacAddress source, ReadOnlySpan<byte> payload)
    {
        byte[] frame = new byte[EthernetFrame.HeaderSize + payload.Length];

        MacAddress.Broadcast.WriteTo(frame);
        source.WriteTo(frame.AsSpan(6));
        frame[12] = 0x08; frame[13] = 0x00; // IPv4
        payload.CopyTo(frame.AsSpan(EthernetFrame.HeaderSize));

        return frame;
    }

    private static int FindFreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private sealed class FakeNode : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly NetworkKeys _keys;

        public NodeId NodeId { get; } = NodeId.CreateRandom();
        public MacAddress Mac { get; } = MacAddress.CreateRandomLocal();
        public FrameCipher Cipher { get; }

        public FakeNode(NetworkKeys keys)
        {
            _keys = keys;
            Cipher = new FrameCipher(keys, NodeId);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }

        public async Task<byte> RegisterAsync(IPEndPoint relay)
        {
            byte[] buffer = new byte[Wire.MaxPacketSize];
            int length = Wire.WriteRegister(buffer, _keys.NetworkId, NodeId, Mac, []);

            await _socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, relay);

            var ack = await ReceiveAsync(PacketType.RegisterAck);
            Assert.True(Wire.TryReadRegisterAck(Wire.Body(ack.Span), out byte index, out _));
            return index;
        }

        public async Task SendDataRelayAsync(IPEndPoint relay, NodeId destination, byte[] frame)
        {
            byte[] packet = new byte[Wire.MaxPacketSize];
            Wire.WriteDataRelayHeader(packet, _keys.NetworkId, NodeId, destination);
            int length = Cipher.Seal(packet, Wire.DataRelayPayloadOffset, frame);

            await _socket.SendToAsync(packet.AsMemory(0, length), SocketFlags.None, relay);
        }

        /// <summary>Descarta pacotes de outros tipos até encontrar o esperado, ou estourar o prazo.</summary>
        public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(PacketType expected, TimeSpan? timeout = null)
        {
            using var cts = new CancellationTokenSource(timeout ?? Timeout);
            byte[] buffer = new byte[Wire.MaxPacketSize];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cts.Token);
                var packet = buffer.AsMemory(0, result.ReceivedBytes);

                if (PacketHeader.TryRead(packet.Span, out var type) && type == expected)
                    return packet.ToArray();
            }
        }

        public void Dispose()
        {
            Cipher.Dispose();
            _socket.Dispose();
        }
    }
}
