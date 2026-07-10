using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using VirtualLan.Core.Net;
using VirtualLan.Core.Protocol;

namespace VirtualLan.Node.Peers;

public enum PathState
{
    /// <summary>Ainda não houve troca direta: tudo passa pelo relay.</summary>
    Relayed,

    /// <summary>Sondas de hole punching em voo.</summary>
    Punching,

    /// <summary>Caminho direto confirmado nos dois sentidos.</summary>
    Direct,
}

/// <summary>
/// Estado por peer, incluindo a máquina de estados do caminho (relay ⇄ direto).
///
/// Todos os campos mutáveis são acessados por múltiplos laços (TAP, socket, manutenção),
/// então só se lê/escreve via <see cref="Volatile"/>/<see cref="Interlocked"/>.
/// </summary>
public sealed class Peer(PeerRecord record)
{
    /// <summary>Sem tráfego direto por este tempo ⇒ o caminho direto morreu; volta para o relay.</summary>
    private static readonly long DirectIdleTimeoutTicks = 10 * Stopwatch.Frequency;

    /// <summary>Intervalo entre sondas enquanto não há caminho direto.</summary>
    private static readonly long PunchIntervalTicks = 1 * Stopwatch.Frequency;

    /// <summary>Intervalo de keepalive no caminho direto — precisa ser menor que o timeout de NAT (~30 s).</summary>
    private static readonly long DirectKeepaliveTicks = 15 * Stopwatch.Frequency;

    private IPEndPoint? _directEndpoint;
    private long _lastDirectActivity;
    private long _lastPunchSent;
    private uint _punchNonce = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private PeerRecord _record = record;

    public NodeId NodeId { get; } = record.NodeId;

    public PeerRecord Record
    {
        get => Volatile.Read(ref _record);
        set => Volatile.Write(ref _record, value);
    }

    public MacAddress Mac => Record.Mac;
    public byte Index => Record.Index;

    public uint PunchNonce => Volatile.Read(ref _punchNonce);

    public PathState State
    {
        get
        {
            if (DirectEndpointIfAlive is not null) return PathState.Direct;
            return Volatile.Read(ref _lastPunchSent) == 0 ? PathState.Relayed : PathState.Punching;
        }
    }

    /// <summary>Endpoint direto, ou null se nunca houve ou se ficou ocioso tempo demais.</summary>
    public IPEndPoint? DirectEndpointIfAlive
    {
        get
        {
            var endpoint = Volatile.Read(ref _directEndpoint);
            if (endpoint is null) return null;

            long idle = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastDirectActivity);
            if (idle <= DirectIdleTimeoutTicks) return endpoint;

            // Expirou: derruba o caminho e volta ao relay. O laço de manutenção reabre o punch.
            Interlocked.CompareExchange(ref _directEndpoint, null, endpoint);
            return null;
        }
    }

    /// <summary>Chamado ao receber um PunchAck válido ou um DataDirect que decifrou com sucesso.</summary>
    public bool PromoteToDirect(IPEndPoint endpoint)
    {
        var previous = Interlocked.Exchange(ref _directEndpoint, endpoint);
        Volatile.Write(ref _lastDirectActivity, Stopwatch.GetTimestamp());

        return previous is null || !previous.Equals(endpoint);
    }

    /// <summary>Renova o caminho direto (recebemos algo autêntico por ele).</summary>
    public void TouchDirect() => Volatile.Write(ref _lastDirectActivity, Stopwatch.GetTimestamp());

    /// <summary>
    /// Decide se é hora de mandar sondas. Enquanto relayed, sonda a cada 1 s; quando direto,
    /// a cada 15 s só para manter o buraco no NAT aberto.
    /// </summary>
    public bool ShouldPunchNow()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastPunchSent);
        long interval = DirectEndpointIfAlive is not null ? DirectKeepaliveTicks : PunchIntervalTicks;

        if (now - last < interval) return false;

        Volatile.Write(ref _lastPunchSent, now);
        return true;
    }

    /// <summary>Endpoints que vale a pena sondar neste ciclo.</summary>
    public IEnumerable<IPEndPoint> PunchTargets()
    {
        var direct = DirectEndpointIfAlive;
        if (direct is not null)
        {
            yield return direct;
            yield break;
        }

        foreach (var candidate in Record.CandidateEndpoints()) yield return candidate;
    }

    public bool MatchesPunchNonce(uint nonce) => nonce == Volatile.Read(ref _punchNonce);

    public override string ToString()
    {
        var direct = DirectEndpointIfAlive;
        string path = direct is not null ? $"direto {direct}" : "via relay";
        return $"25.0.0.{Index}  {Mac}  {NodeId.ToShortString()}  {path}";
    }
}
