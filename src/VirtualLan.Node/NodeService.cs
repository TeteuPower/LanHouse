using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VirtualLan.Core.Crypto;
using VirtualLan.Core.Diagnostics;
using VirtualLan.Core.Net;
using VirtualLan.Core.Protocol;
using VirtualLan.Node.Peers;
using VirtualLan.Node.Tap;

namespace VirtualLan.Node;

/// <summary>
/// O nó: liga o adaptador TAP à rede virtual.
///
/// Três laços concorrentes:
///   TAP  → rede : lê quadros do jogo, decide unicast/flood, cifra e envia.
///   rede → TAP  : decifra, aprende MAC de origem, injeta na pilha do Windows.
///   manutenção  : registro, keepalive, hole punching, expiração da tabela MAC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NodeService(NodeOptions options) : IDisposable
{
    private static readonly TimeSpan RegisterRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaintenanceTick = TimeSpan.FromMilliseconds(500);

    private readonly NodeOptions _options = options;
    private readonly NodeId _nodeId = NodeId.CreateRandom();
    private readonly ConcurrentDictionary<NodeId, Peer> _peers = new();
    private readonly MacTable _macTable = new();

    private NetworkKeys _keys = null!;
    private FrameCipher _cipher = null!;
    private TapDevice _tap = null!;
    private Socket _socket = null!;
    private IPEndPoint _relayEndpoint = null!;

    private volatile bool _registered;
    private byte _assignedIndex;
    private DateTime _lastRegisterSentUtc = DateTime.MinValue;
    private DateTime _lastKeepaliveSentUtc = DateTime.MinValue;

    private long _framesToNetwork;
    private long _framesToTap;

    // ---- Observação para a GUI (a lógica de rede não depende disto) --------------------------

    /// <summary>Disparado a cada mudança de estágio de conexão. Handler pode vir de outra thread.</summary>
    public event Action<NodeConnectionState, string?>? StateChanged;

    /// <summary>Estágio atual da conexão.</summary>
    public NodeConnectionState State { get; private set; } = NodeConnectionState.Idle;

    /// <summary>IP virtual atribuído pelo relay, ou null enquanto não registrado.</summary>
    public IPAddress? VirtualAddress { get; private set; }

    /// <summary>networkId curto (o relay agrupa sessões por ele). Útil para conferir se bate com o peer.</summary>
    public string NetworkIdShort => _keys?.NetworkId.ToShortString() ?? "—";

    public long FramesToNetwork => Volatile.Read(ref _framesToNetwork);
    public long FramesToTap => Volatile.Read(ref _framesToTap);
    public int PeerCount => _peers.Count;

    private void SetState(NodeConnectionState state, string? detail = null)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    /// <summary>Fotografia dos peers para a GUI. Ordenada por índice (IP virtual).</summary>
    public IReadOnlyList<PeerView> SnapshotPeers()
    {
        var list = new List<PeerView>(_peers.Count);

        foreach (var peer in _peers.Values)
        {
            var direct = peer.DirectEndpointIfAlive;
            list.Add(new PeerView(
                peer.Index,
                $"25.0.0.{peer.Index}",
                peer.Mac.ToString(),
                peer.NodeId.ToShortString(),
                peer.State,
                direct is not null ? $"direto {direct}" : "via relay"));
        }

        list.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return list;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        SetState(NodeConnectionState.Resolving);

        _keys = NetworkKeys.Derive(_options.NetworkName, _options.Password);
        _cipher = new FrameCipher(_keys, _nodeId);

        Log.Info($"Rede '{_options.NetworkName}' → networkId {_keys.NetworkId.ToShortString()}");
        Log.Info($"Node {_nodeId.ToShortString()} (efêmero)");

        _relayEndpoint = await ResolveRelayAsync(_options.RelayHost, _options.RelayPort, cancellationToken)
            .ConfigureAwait(false);

        _tap = TapDevice.Open(TapAdapterLocator.Select(_options.AdapterName));
        _socket = CreateSocket(_options.LocalPort);

        var boundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        Log.Info($"Socket UDP local em 0.0.0.0:{boundPort}, relay em {_relayEndpoint}");

        SetState(NodeConnectionState.Connecting);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var socketLoop = SocketReceiveLoopAsync(linked.Token);
        var tapLoop = TapReceiveLoopAsync(linked.Token);
        var maintenanceLoop = MaintenanceLoopAsync(boundPort, linked.Token);

        try
        {
            await Task.WhenAny(socketLoop, tapLoop, maintenanceLoop).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await SendDisconnectAsync().ConfigureAwait(false);

            // Aguarda os laços drenarem, sem propagar o cancelamento como erro.
            await Task.WhenAll(
                Swallow(socketLoop), Swallow(tapLoop), Swallow(maintenanceLoop)).ConfigureAwait(false);
        }

        static async Task Swallow(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Debug($"Laço terminou com {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------------ Infraestrutura

    private static async Task<IPEndPoint> ResolveRelayAsync(string host, int port, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return new IPEndPoint(literal, port);

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        if (addresses.Length == 0) throw new InvalidOperationException($"Não foi possível resolver '{host}' para IPv4.");

        return new IPEndPoint(addresses[0], port);
    }

    private static Socket CreateSocket(int localPort)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveBufferSize = 1 << 20,
            SendBufferSize = 1 << 20,
        };

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Um peer que saiu gera ICMP port-unreachable; sem isto o próximo Receive lança
                // ConnectionReset e mata o laço. É a pegadinha clássica de UDP no Windows.
                const int SIO_UDP_CONNRESET = -1744830452;
                socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
            }

            socket.Bind(new IPEndPoint(IPAddress.Any, localPort));
            return socket;
        }
        catch
        {
            // Falha ao configurar/bindar (ex.: porta em uso): não vaza o handle nativo.
            socket.Dispose();
            throw;
        }
    }

    // -------------------------------------------------------------------------- TAP → rede

    private async Task TapReceiveLoopAsync(CancellationToken ct)
    {
        byte[] frame = GC.AllocateArray<byte>(Wire.MaxFrameSize, pinned: true);
        byte[] packet = GC.AllocateArray<byte>(Wire.MaxPacketSize, pinned: true);

        while (!ct.IsCancellationRequested)
        {
            int length = await _tap.ReadFrameAsync(frame, ct).ConfigureAwait(false);
            if (!EthernetFrame.IsValid(frame.AsSpan(0, length))) continue;

            var destination = EthernetFrame.GetDestination(frame.AsSpan(0, length));

            if (destination.IsGroupAddress)
            {
                // É aqui que a mágica acontece: o broadcast do jogo (descoberta de sala)
                // é replicado para todos os peers, como faria um switch físico.
                await FloodAsync(frame.AsMemory(0, length), packet, ct).ConfigureAwait(false);
            }
            else if (_macTable.TryResolve(destination, out var nodeId) && _peers.TryGetValue(nodeId, out var peer))
            {
                await SendFrameToPeerAsync(peer, frame.AsMemory(0, length), packet, ct).ConfigureAwait(false);
            }
            else
            {
                // MAC desconhecido: um switch inunda em vez de descartar. Só assim o primeiro
                // pacote de uma conversa nova chega antes de a tabela ser aprendida.
                await FloodAsync(frame.AsMemory(0, length), packet, ct).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _framesToNetwork);
        }
    }

    private async ValueTask FloodAsync(ReadOnlyMemory<byte> frame, byte[] packet, CancellationToken ct)
    {
        var peers = _peers.Values;
        if (peers.Count == 0) return;

        int directCount = 0;
        foreach (var peer in peers)
        {
            if (peer.DirectEndpointIfAlive is null) continue;

            directCount++;
            await SendDirectAsync(peer, frame, packet, ct).ConfigureAwait(false);
        }

        int relayedCount = peers.Count - directCount;
        if (relayedCount == 0) return;

        if (directCount == 0 && relayedCount > 1)
        {
            // Ninguém tem caminho direto: um único DataRelay com destino "zero" faz o relay
            // replicar. Economiza banda de subida sem duplicar quadros.
            await SendRelayAsync(NodeId.Zero, frame, packet, ct).ConfigureAwait(false);
            return;
        }

        // Caso misto: unicast pelo relay, peer a peer. Um broadcast pelo relay entregaria
        // cópia duplicada a quem já recebeu pelo caminho direto — e quadro duplicado faz o
        // jogo listar a mesma sala duas vezes.
        foreach (var peer in peers)
        {
            if (peer.DirectEndpointIfAlive is not null) continue;
            await SendRelayAsync(peer.NodeId, frame, packet, ct).ConfigureAwait(false);
        }
    }

    private ValueTask SendFrameToPeerAsync(Peer peer, ReadOnlyMemory<byte> frame, byte[] packet, CancellationToken ct)
        => peer.DirectEndpointIfAlive is not null
            ? SendDirectAsync(peer, frame, packet, ct)
            : SendRelayAsync(peer.NodeId, frame, packet, ct);

    private async ValueTask SendDirectAsync(Peer peer, ReadOnlyMemory<byte> frame, byte[] packet, CancellationToken ct)
    {
        var endpoint = peer.DirectEndpointIfAlive;
        if (endpoint is null) return;

        Wire.WriteDataDirectHeader(packet, _nodeId);
        int length = _cipher.Seal(packet, Wire.DataDirectPayloadOffset, frame.Span);

        await SendToAsync(packet.AsMemory(0, length), endpoint, ct).ConfigureAwait(false);
    }

    private async ValueTask SendRelayAsync(NodeId destination, ReadOnlyMemory<byte> frame, byte[] packet, CancellationToken ct)
    {
        Wire.WriteDataRelayHeader(packet, _keys.NetworkId, _nodeId, destination);
        int length = _cipher.Seal(packet, Wire.DataRelayPayloadOffset, frame.Span);

        await SendToAsync(packet.AsMemory(0, length), _relayEndpoint, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------- rede → TAP

    private async Task SocketReceiveLoopAsync(CancellationToken ct)
    {
        byte[] packet = GC.AllocateArray<byte>(Wire.MaxPacketSize, pinned: true);
        byte[] frame = GC.AllocateArray<byte>(Wire.MaxFrameSize, pinned: true);
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(packet, SocketFlags.None, remote, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                Log.Debug($"recvfrom: {ex.SocketErrorCode}");
                continue;
            }

            var source = (IPEndPoint)result.RemoteEndPoint;
            var received = packet.AsMemory(0, result.ReceivedBytes);

            if (!PacketHeader.TryRead(received.Span, out var type)) continue;

            switch (type)
            {
                case PacketType.RegisterAck: HandleRegisterAck(received.Span); break;
                case PacketType.PeerUpdate: HandlePeerUpdate(received.Span); break;
                case PacketType.Punch: await HandlePunchAsync(received, source, ct).ConfigureAwait(false); break;
                case PacketType.PunchAck: HandlePunchAck(received.Span, source); break;
                case PacketType.DataDirect: await HandleDataDirectAsync(received, frame, source, ct).ConfigureAwait(false); break;
                case PacketType.DataRelay: await HandleDataRelayAsync(received, frame, source, ct).ConfigureAwait(false); break;
                case PacketType.Error: HandleError(received.Span); break;
                default: Log.Trace($"Tipo {type} inesperado de {source}."); break;
            }
        }
    }

    private async ValueTask HandleDataDirectAsync(
        ReadOnlyMemory<byte> packet, byte[] frame, IPEndPoint source, CancellationToken ct)
    {
        if (!Wire.TryReadDataDirectHeader(packet.Span, out var sourceNodeId)) return;
        if (!_peers.TryGetValue(sourceNodeId, out var peer)) return;

        if (!_cipher.TryOpen(packet.Span, Wire.DataDirectPayloadOffset, frame, out int length)) return;

        // Só depois de a tag GCM validar é que confiamos no endpoint de origem. Isso impede
        // que um terceiro sequestre o caminho direto forjando datagramas.
        if (peer.PromoteToDirect(source))
            Log.Info($"Caminho direto estabelecido com 25.0.0.{peer.Index} via {source}");
        else
            peer.TouchDirect();

        await DeliverToTapAsync(peer, frame.AsMemory(0, length), ct).ConfigureAwait(false);
    }

    private async ValueTask HandleDataRelayAsync(
        ReadOnlyMemory<byte> packet, byte[] frame, IPEndPoint source, CancellationToken ct)
    {
        if (!source.Equals(_relayEndpoint)) return;
        if (!Wire.TryReadDataRelayHeader(packet.Span, out var networkId, out var sourceNodeId, out _)) return;
        if (networkId != _keys.NetworkId) return;
        if (!_peers.TryGetValue(sourceNodeId, out var peer)) return;

        if (!_cipher.TryOpen(packet.Span, Wire.DataRelayPayloadOffset, frame, out int length)) return;

        await DeliverToTapAsync(peer, frame.AsMemory(0, length), ct).ConfigureAwait(false);
    }

    private async ValueTask DeliverToTapAsync(Peer peer, ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (!EthernetFrame.IsValid(frame.Span)) return;

        // Aprendizado do switch: agora sabemos por onde alcançar este MAC.
        _macTable.Learn(EthernetFrame.GetSource(frame.Span), peer.NodeId);

        await _tap.WriteFrameAsync(frame, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _framesToTap);
    }

    // ---------------------------------------------------------------------- Plano de controle

    private void HandleRegisterAck(ReadOnlySpan<byte> packet)
    {
        if (!Wire.TryReadRegisterAck(Wire.Body(packet), out byte index, out var peers)) return;

        if (!_registered)
        {
            _assignedIndex = index;
            _registered = true;

            var address = _options.AddressForIndex(index);

            NetworkConfigurator.ConfigureInterface(_tap.Adapter.Name, address, _options.SubnetMask);
            NetworkConfigurator.TrySetPrivateProfile(_tap.Adapter.Name);
            NetworkConfigurator.EnsureFirewallRule(NodeOptions.FirewallRuleName, _options.SubnetBase, _options.SubnetMask);

            VirtualAddress = address;
            Log.Info($"Registrado. Seu IP virtual é {address}");
            SetState(NodeConnectionState.Connected, address.ToString());
        }
        else if (index != _assignedIndex)
        {
            Log.Warn($"Relay reatribuiu o índice {_assignedIndex} → {index}. Reconfigure o adaptador reiniciando o vlan.");
        }

        MergePeers(peers);
    }

    private void HandlePeerUpdate(ReadOnlySpan<byte> packet)
    {
        if (!Wire.TryReadPeerUpdate(Wire.Body(packet), out var peers)) return;
        MergePeers(peers);
    }

    /// <summary>Sincroniza o dicionário local com a verdade do relay: adiciona, atualiza e remove.</summary>
    private void MergePeers(List<PeerRecord> records)
    {
        var seen = new HashSet<NodeId>(records.Count);

        foreach (var record in records)
        {
            seen.Add(record.NodeId);

            if (_peers.TryGetValue(record.NodeId, out var existing))
            {
                existing.Record = record;
            }
            else
            {
                _peers[record.NodeId] = new Peer(record);
                Log.Info($"Peer entrou: 25.0.0.{record.Index} [{record.Mac}] {record.NodeId.ToShortString()}");
            }
        }

        foreach (var nodeId in _peers.Keys)
        {
            if (seen.Contains(nodeId)) continue;

            if (_peers.TryRemove(nodeId, out var removed))
            {
                _macTable.ForgetNode(nodeId);
                Log.Info($"Peer saiu: 25.0.0.{removed.Index} {nodeId.ToShortString()}");
            }
        }
    }

    // ReadOnlyMemory, não ReadOnlySpan: um parâmetro ref struct não pode existir em método async.
    private async ValueTask HandlePunchAsync(ReadOnlyMemory<byte> packet, IPEndPoint source, CancellationToken ct)
    {
        if (!Wire.TryReadPunch(Wire.Body(packet.Span), out var senderNodeId, out uint nonce)) return;
        if (!_peers.ContainsKey(senderNodeId)) return;

        // Responder é o que abre o buraco no NOSSO NAT para o endpoint dele.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            int length = Wire.WritePunchAck(buffer, _nodeId, nonce);
            await SendToAsync(buffer.AsMemory(0, length), source, ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandlePunchAck(ReadOnlySpan<byte> packet, IPEndPoint source)
    {
        if (!Wire.TryReadPunch(Wire.Body(packet), out var senderNodeId, out uint nonce)) return;
        if (!_peers.TryGetValue(senderNodeId, out var peer)) return;

        // O nonce prova que este ack responde a uma sonda nossa, e não a um replay antigo.
        if (!peer.MatchesPunchNonce(nonce)) return;

        if (peer.PromoteToDirect(source))
            Log.Info($"Caminho direto estabelecido com 25.0.0.{peer.Index} via {source}");
    }

    private static void HandleError(ReadOnlySpan<byte> packet)
    {
        if (Wire.TryReadError(Wire.Body(packet), out var code, out string message))
            Log.Error($"Relay recusou: {code} — {message}");
    }

    // ------------------------------------------------------------------------- Manutenção

    private async Task MaintenanceLoopAsync(int localPort, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(MaintenanceTick);
        byte[] buffer = GC.AllocateArray<byte>(Wire.MaxPacketSize, pinned: true);
        var lastStatus = DateTime.UtcNow;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = DateTime.UtcNow;

            if (!_registered && now - _lastRegisterSentUtc >= RegisterRetryInterval)
            {
                _lastRegisterSentUtc = now;

                var local = LocalEndpointDiscovery.Discover(localPort, _tap.Adapter.Name);
                int length = Wire.WriteRegister(buffer, _keys.NetworkId, _nodeId, _tap.MacAddress, local);

                await SendToAsync(buffer.AsMemory(0, length), _relayEndpoint, ct).ConfigureAwait(false);
                Log.Debug($"Register enviado ao relay ({local.Count} endpoints locais).");
            }

            if (_registered && now - _lastKeepaliveSentUtc >= KeepaliveInterval)
            {
                _lastKeepaliveSentUtc = now;

                int length = Wire.WriteKeepalive(buffer, _keys.NetworkId, _nodeId);
                await SendToAsync(buffer.AsMemory(0, length), _relayEndpoint, ct).ConfigureAwait(false);
            }

            foreach (var peer in _peers.Values)
            {
                if (!peer.ShouldPunchNow()) continue;

                int length = Wire.WritePunch(buffer, _nodeId, peer.PunchNonce);

                foreach (var target in peer.PunchTargets())
                    await SendToAsync(buffer.AsMemory(0, length), target, ct).ConfigureAwait(false);
            }

            _macTable.RemoveExpired();

            if (now - lastStatus >= TimeSpan.FromSeconds(30))
            {
                lastStatus = now;
                LogStatus();
            }
        }
    }

    private void LogStatus()
    {
        if (_peers.IsEmpty)
        {
            Log.Info("Nenhum peer online. Peça para o outro lado entrar na mesma rede/senha.");
            return;
        }

        Log.Info($"— {_peers.Count} peer(s), tx={Volatile.Read(ref _framesToNetwork)} rx={Volatile.Read(ref _framesToTap)} macs={_macTable.Count}");
        foreach (var peer in _peers.Values) Log.Info($"   {peer}");
    }

    private async ValueTask SendToAsync(ReadOnlyMemory<byte> packet, IPEndPoint destination, CancellationToken ct)
    {
        try
        {
            await _socket.SendToAsync(packet, SocketFlags.None, destination, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            Log.Trace($"sendto {destination}: {ex.SocketErrorCode}");
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task SendDisconnectAsync()
    {
        if (!_registered) return;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
        try
        {
            int length = Wire.WriteDisconnect(buffer, _keys.NetworkId, _nodeId);
            await _socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, _relayEndpoint, CancellationToken.None)
                         .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"Disconnect não enviado: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        // Os campos são preenchidos em RunAsync; Dispose pode ser chamado antes disso.
        if (_registered) NetworkConfigurator.RemoveFirewallRule(NodeOptions.FirewallRuleName);

        if (_cipher is not null) _cipher.Dispose();
        if (_tap is not null) _tap.Dispose();
        if (_socket is not null) _socket.Dispose();
    }
}
