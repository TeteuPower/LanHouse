using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using VirtualLan.Core.Diagnostics;
using VirtualLan.Core.Protocol;

namespace VirtualLan.Relay;

/// <summary>
/// Servidor de rendezvous + relay de dados.
///
/// Responsabilidades:
///   1. Observar o endpoint público de cada nó (o mapeamento NAT) e divulgá-lo aos peers.
///   2. Encaminhar quadros cifrados enquanto os peers não conseguem falar direto.
///
/// O que ele deliberadamente NÃO faz: decifrar, inspecionar ou armazenar tráfego.
/// Ele conhece apenas <c>networkId</c>, que é uma derivação HKDF da senha com um
/// <c>info</c> distinto do usado para a chave de dados.
/// </summary>
public sealed class RelayServer(int port) : IDisposable
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<NetworkId, NetworkSession> _networks = new();
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    private long _packetsForwarded;
    private long _bytesForwarded;

    public int Port { get; } = port;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ConfigureSocket();
        _socket.Bind(new IPEndPoint(IPAddress.Any, Port));

        Log.Info($"Relay ouvindo em 0.0.0.0:{Port}/udp");

        var maintenance = MaintenanceLoopAsync(cancellationToken);
        var receive = ReceiveLoopAsync(cancellationToken);

        await Task.WhenAll(maintenance, receive).ConfigureAwait(false);
    }

    private void ConfigureSocket()
    {
        _socket.ReceiveBufferSize = 1 << 20;
        _socket.SendBufferSize = 1 << 20;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Sem isso, um ICMP "port unreachable" vindo de um peer que saiu derruba o socket
            // inteiro com ConnectionReset na próxima leitura. Comportamento só do Windows.
            const int SIO_UDP_CONNRESET = -1744830452;
            _socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = GC.AllocateArray<byte>(Wire.MaxPacketSize, pinned: true);
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, cancellationToken)
                                      .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                Log.Debug($"recvfrom falhou: {ex.SocketErrorCode}");
                continue;
            }

            var source = (IPEndPoint)result.RemoteEndPoint;

            try
            {
                await HandlePacketAsync(buffer.AsMemory(0, result.ReceivedBytes), source, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Entrada não-confiável: um pacote ruim jamais pode derrubar o servidor.
                Log.Warn($"Erro processando pacote de {source}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task HandlePacketAsync(ReadOnlyMemory<byte> packet, IPEndPoint source, CancellationToken ct)
    {
        if (!PacketHeader.TryRead(packet.Span, out var type))
        {
            if (PacketHeader.TryPeekVersion(packet.Span, out byte version) && version != PacketHeader.Version)
                await SendErrorAsync(source, ErrorCode.ProtocolVersionMismatch, $"Servidor fala v{PacketHeader.Version}.", ct)
                    .ConfigureAwait(false);

            return; // lixo da internet: descarta em silêncio
        }

        switch (type)
        {
            case PacketType.Register:
                await HandleRegisterAsync(packet, source, ct).ConfigureAwait(false);
                break;

            case PacketType.Keepalive:
                HandleKeepalive(packet, source);
                break;

            case PacketType.Disconnect:
                await HandleDisconnectAsync(packet, ct).ConfigureAwait(false);
                break;

            case PacketType.DataRelay:
                await HandleDataRelayAsync(packet, source, ct).ConfigureAwait(false);
                break;

            default:
                Log.Trace($"Tipo {type} não é para o relay (de {source}).");
                break;
        }
    }

    // ------------------------------------------------------------------------------ Register

    private async Task HandleRegisterAsync(ReadOnlyMemory<byte> packet, IPEndPoint source, CancellationToken ct)
    {
        if (!Wire.TryReadRegister(Wire.Body(packet.Span), out var networkId, out var nodeId, out var mac, out var localEndpoints))
        {
            await SendErrorAsync(source, ErrorCode.MalformedPacket, "Register inválido.", ct).ConfigureAwait(false);
            return;
        }

        var network = _networks.GetOrAdd(networkId, _ => new NetworkSession());

        if (!network.Upsert(nodeId, mac, source, localEndpoints, out var session, out bool isNew))
        {
            await SendErrorAsync(source, ErrorCode.NetworkFull, "Rede cheia (254 nós).", ct).ConfigureAwait(false);
            return;
        }

        if (isNew)
            Log.Info($"[{networkId.ToShortString()}] + {nodeId.ToShortString()} idx={session.Index} de {source} ({network.Nodes.Length} nós)");
        else
            Log.Debug($"[{networkId.ToShortString()}] ~ {nodeId.ToShortString()} re-registrado de {source}");

        // ACK com a lista de peers atual.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Wire.MaxPacketSize);
        try
        {
            int length = Wire.WriteRegisterAck(buffer, session.Index, network.PeersExcept(nodeId));
            await SendAsync(buffer.AsMemory(0, length), source, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Avisa os demais que a topologia mudou, para que iniciem o hole punching contra o novato.
        await BroadcastPeerUpdateAsync(network, exceptNodeId: nodeId, ct).ConfigureAwait(false);
    }

    private async Task BroadcastPeerUpdateAsync(NetworkSession network, NodeId exceptNodeId, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Wire.MaxPacketSize);
        try
        {
            foreach (var node in network.Nodes)
            {
                if (node.NodeId == exceptNodeId) continue;

                int length = Wire.WritePeerUpdate(buffer, network.PeersExcept(node.NodeId));
                await SendAsync(buffer.AsMemory(0, length), node.PublicEndpoint, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ----------------------------------------------------------------------------- Keepalive

    private void HandleKeepalive(ReadOnlyMemory<byte> packet, IPEndPoint source)
    {
        if (!Wire.TryReadNetworkAndNode(Wire.Body(packet.Span), out var networkId, out var nodeId)) return;
        if (!_networks.TryGetValue(networkId, out var network)) return;
        if (!network.TryGetNode(nodeId, out var session)) return;

        session.LastSeenUtc = DateTime.UtcNow;

        // O NAT pode ter reciclado a porta. Seguir o endpoint de origem mantém o relay utilizável.
        if (!session.PublicEndpoint.Equals(source))
        {
            Log.Debug($"[{networkId.ToShortString()}] {nodeId.ToShortString()} migrou {session.PublicEndpoint} → {source}");
            session.PublicEndpoint = source;
        }
    }

    // ---------------------------------------------------------------------------- Disconnect

    private async Task HandleDisconnectAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        if (!Wire.TryReadNetworkAndNode(Wire.Body(packet.Span), out var networkId, out var nodeId)) return;
        if (!_networks.TryGetValue(networkId, out var network)) return;
        if (!network.Remove(nodeId)) return;

        Log.Info($"[{networkId.ToShortString()}] - {nodeId.ToShortString()} saiu ({network.Nodes.Length} nós)");

        if (network.IsEmpty) _networks.TryRemove(networkId, out _);
        else await BroadcastPeerUpdateAsync(network, exceptNodeId: nodeId, ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------------- DataRelay

    private async Task HandleDataRelayAsync(ReadOnlyMemory<byte> packet, IPEndPoint source, CancellationToken ct)
    {
        if (!Wire.TryReadDataRelayHeader(packet.Span, out var networkId, out var srcNodeId, out var dstNodeId)) return;
        if (!_networks.TryGetValue(networkId, out var network)) return;

        // Anti-amplificação: só encaminha para quem já provou estar na rede, e apenas se o
        // datagrama veio do endpoint que aquele nó registrou.
        if (!network.TryGetNode(srcNodeId, out var sender)) return;
        if (!sender.PublicEndpoint.Equals(source)) return;

        sender.LastSeenUtc = DateTime.UtcNow;

        // Encaminhamento verbatim: o pacote inteiro é AAD+ciphertext. Qualquer mutação aqui
        // invalidaria a tag GCM no destino — que é exatamente a propriedade que queremos.
        if (dstNodeId.IsZero)
        {
            foreach (var node in network.Nodes)
            {
                if (node.NodeId == srcNodeId) continue;
                await SendAsync(packet, node.PublicEndpoint, ct).ConfigureAwait(false);
            }
        }
        else if (network.TryGetNode(dstNodeId, out var destination))
        {
            await SendAsync(packet, destination.PublicEndpoint, ct).ConfigureAwait(false);
        }
    }

    // --------------------------------------------------------------------------- Manutenção

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MaintenanceInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var (networkId, network) in _networks)
                {
                    var stale = network.RemoveStale(SessionTimeout);

                    foreach (var node in stale)
                        Log.Info($"[{networkId.ToShortString()}] - {node.NodeId.ToShortString()} expirou");

                    if (network.IsEmpty) _networks.TryRemove(networkId, out _);
                    else if (stale.Count > 0) await BroadcastPeerUpdateAsync(network, NodeId.Zero, cancellationToken).ConfigureAwait(false);
                }

                Log.Debug($"redes={_networks.Count} encaminhados={Volatile.Read(ref _packetsForwarded)} bytes={Volatile.Read(ref _bytesForwarded)}");
            }
        }
        catch (OperationCanceledException) { /* shutdown normal */ }
    }

    // ------------------------------------------------------------------------------- Envio

    private async ValueTask SendAsync(ReadOnlyMemory<byte> packet, IPEndPoint destination, CancellationToken ct)
    {
        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, destination, ct).ConfigureAwait(false);

            Interlocked.Increment(ref _packetsForwarded);
            Interlocked.Add(ref _bytesForwarded, packet.Length);
        }
        catch (SocketException ex)
        {
            Log.Debug($"sendto {destination} falhou: {ex.SocketErrorCode}");
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async ValueTask SendErrorAsync(IPEndPoint destination, ErrorCode code, string message, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(512);
        try
        {
            int length = Wire.WriteError(buffer, code, message);
            await SendAsync(buffer.AsMemory(0, length), destination, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose() => _socket.Dispose();
}
