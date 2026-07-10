using System.Collections.Concurrent;
using System.Diagnostics;
using VirtualLan.Core.Protocol;

namespace VirtualLan.Core.Net;

/// <summary>
/// Tabela de encaminhamento de um switch com aprendizado (learning switch).
///
/// Aprende <c>MAC de origem → nó de origem</c> a cada quadro recebido da rede virtual,
/// exatamente como um switch físico aprende MAC → porta. Entradas expiram para acompanhar
/// mudanças de topologia (peer reconecta com novo NodeId).
///
/// Thread-safe e sem locks no caminho de leitura.
/// </summary>
public sealed class MacTable(TimeSpan? entryLifetime = null)
{
    private readonly record struct Entry(NodeId NodeId, long ExpiresAtTicks);

    private readonly ConcurrentDictionary<MacAddress, Entry> _entries = new();
    private readonly long _lifetimeTicks = (long)(entryLifetime ?? TimeSpan.FromMinutes(5)).TotalSeconds * Stopwatch.Frequency;

    public int Count => _entries.Count;

    /// <summary>Registra (ou renova) o mapeamento MAC → nó. Ignora MACs de grupo, que nunca são origem válida.</summary>
    public void Learn(MacAddress source, NodeId nodeId)
    {
        if (source.IsGroupAddress || source.IsZero) return;

        _entries[source] = new Entry(nodeId, Stopwatch.GetTimestamp() + _lifetimeTicks);
    }

    /// <summary>
    /// Resolve o nó dono de um MAC. Retorna false para MAC desconhecido ou expirado,
    /// o que o chamador deve tratar como "flood".
    /// </summary>
    public bool TryResolve(MacAddress destination, out NodeId nodeId)
    {
        nodeId = default;

        if (!_entries.TryGetValue(destination, out var entry)) return false;

        if (Stopwatch.GetTimestamp() > entry.ExpiresAtTicks)
        {
            _entries.TryRemove(destination, out _);
            return false;
        }

        nodeId = entry.NodeId;
        return true;
    }

    /// <summary>Remove todas as entradas de um nó que saiu da rede.</summary>
    public void ForgetNode(NodeId nodeId)
    {
        foreach (var (mac, entry) in _entries)
        {
            if (entry.NodeId == nodeId) _entries.TryRemove(mac, out _);
        }
    }

    /// <summary>Varre e remove entradas expiradas. Chamado pelo laço de manutenção.</summary>
    public int RemoveExpired()
    {
        long now = Stopwatch.GetTimestamp();
        int removed = 0;

        foreach (var (mac, entry) in _entries)
        {
            if (now > entry.ExpiresAtTicks && _entries.TryRemove(mac, out _)) removed++;
        }

        return removed;
    }

    public IEnumerable<(MacAddress Mac, NodeId NodeId)> Snapshot()
        => _entries.Select(kv => (kv.Key, kv.Value.NodeId)).ToArray();
}
