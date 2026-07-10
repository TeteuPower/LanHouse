using System.Net;
using VirtualLan.Core.Net;
using VirtualLan.Core.Protocol;

namespace VirtualLan.Relay;

/// <summary>Um nó conectado, do ponto de vista do relay.</summary>
public sealed class NodeSession
{
    public required NodeId NodeId { get; init; }
    public required byte Index { get; init; }
    public MacAddress Mac { get; set; }

    /// <summary>Endpoint de onde os datagramas chegam: o mapeamento criado pelo NAT do peer.</summary>
    public IPEndPoint PublicEndpoint { get; set; } = null!;

    /// <summary>Endpoints que o nó anunciou ter na própria LAN física.</summary>
    public IReadOnlyList<IPEndPoint> LocalEndpoints { get; set; } = [];

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public PeerRecord ToRecord() => new(
        NodeId,
        Mac,
        Index,
        PublicEndpoint,
        LocalEndpoints.Count > 0 ? LocalEndpoints[0] : null);
}

/// <summary>
/// Todos os nós de um mesmo <see cref="NetworkId"/>. O relay nunca decifra nada aqui —
/// só mantém quem está online e para onde encaminhar.
///
/// Todo acesso é serializado por <see cref="_gate"/>. As operações são O(n) sobre n ≤ 254
/// e acontecem apenas no plano de controle, nunca no caminho quente de dados
/// (que usa <see cref="TryGetNode"/> em snapshot imutável).
/// </summary>
public sealed class NetworkSession
{
    /// <summary>Índices válidos: 1..254. O 0 é endereço de rede e o 255 é broadcast.</summary>
    private const int MaxNodes = 254;

    private readonly object _gate = new();
    private readonly Dictionary<NodeId, NodeSession> _nodes = [];
    private readonly bool[] _indexInUse = new bool[256];

    private volatile NodeSession[] _snapshot = [];

    /// <summary>Cópia imutável usada sem lock pelo caminho de dados.</summary>
    public NodeSession[] Nodes => _snapshot;

    public bool IsEmpty => _snapshot.Length == 0;

    /// <summary>
    /// Registra um nó novo ou atualiza um existente (re-register após troca de IP, por exemplo).
    /// Retorna false se a rede estiver cheia.
    /// </summary>
    public bool Upsert(
        NodeId nodeId,
        MacAddress mac,
        IPEndPoint publicEndpoint,
        IReadOnlyList<IPEndPoint> localEndpoints,
        out NodeSession session,
        out bool isNew)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(nodeId, out var existing))
            {
                existing.Mac = mac;
                existing.PublicEndpoint = publicEndpoint;
                existing.LocalEndpoints = localEndpoints;
                existing.LastSeenUtc = DateTime.UtcNow;

                session = existing;
                isNew = false;
                return true;
            }

            if (!TryAllocateIndex(out byte index))
            {
                session = null!;
                isNew = false;
                return false;
            }

            session = new NodeSession
            {
                NodeId = nodeId,
                Index = index,
                Mac = mac,
                PublicEndpoint = publicEndpoint,
                LocalEndpoints = localEndpoints,
            };

            _nodes.Add(nodeId, session);
            _indexInUse[index] = true;
            RebuildSnapshot();

            isNew = true;
            return true;
        }
    }

    public bool TryGetNode(NodeId nodeId, out NodeSession session)
    {
        // Leitura sem lock: o snapshot é substituído por inteiro, nunca mutado.
        foreach (var node in _snapshot)
        {
            if (node.NodeId == nodeId)
            {
                session = node;
                return true;
            }
        }

        session = null!;
        return false;
    }

    public bool Remove(NodeId nodeId)
    {
        lock (_gate)
        {
            if (!_nodes.Remove(nodeId, out var removed)) return false;

            _indexInUse[removed.Index] = false;
            RebuildSnapshot();
            return true;
        }
    }

    /// <summary>Remove nós silenciosos há mais de <paramref name="timeout"/>.</summary>
    public List<NodeSession> RemoveStale(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;
        List<NodeSession> removed = [];

        lock (_gate)
        {
            foreach (var node in _nodes.Values.Where(n => n.LastSeenUtc < cutoff).ToList())
            {
                _nodes.Remove(node.NodeId);
                _indexInUse[node.Index] = false;
                removed.Add(node);
            }

            if (removed.Count > 0) RebuildSnapshot();
        }

        return removed;
    }

    /// <summary>Lista de peers do ponto de vista de <paramref name="exceptNodeId"/> (que não se vê na lista).</summary>
    public List<PeerRecord> PeersExcept(NodeId exceptNodeId)
    {
        var snapshot = _snapshot;
        List<PeerRecord> peers = new(snapshot.Length);

        foreach (var node in snapshot)
        {
            if (node.NodeId != exceptNodeId) peers.Add(node.ToRecord());
        }

        return peers;
    }

    private bool TryAllocateIndex(out byte index)
    {
        for (int i = 1; i <= MaxNodes; i++)
        {
            if (!_indexInUse[i])
            {
                index = (byte)i;
                return true;
            }
        }

        index = 0;
        return false;
    }

    private void RebuildSnapshot() => _snapshot = [.. _nodes.Values];
}
