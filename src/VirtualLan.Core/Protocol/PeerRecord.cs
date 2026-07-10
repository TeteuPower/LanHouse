using System.Diagnostics.CodeAnalysis;
using System.Net;
using VirtualLan.Core.Net;

namespace VirtualLan.Core.Protocol;

/// <summary>
/// Descrição de um peer conforme conhecida pelo relay.
/// Serializa em 35 bytes: nodeId(16) mac(6) index(1) public(6) local(6).
///
/// É uma <b>classe</b> (não struct) de propósito: o <c>Peer</c> troca a referência inteira
/// quando o relay anuncia novos endpoints, e trocar uma referência é atômico. Um struct de
/// 35 bytes exigiria lock para evitar leitura rasgada no caminho quente.
/// </summary>
/// <param name="NodeId">Identidade efêmera do peer.</param>
/// <param name="Mac">MAC virtual do adaptador TAP do peer.</param>
/// <param name="Index">Último octeto do IP virtual (25.0.0.<c>Index</c>).</param>
/// <param name="PublicEndpoint">Endpoint observado pelo relay (o mapeamento do NAT).</param>
/// <param name="LocalEndpoint">Endpoint na LAN física do peer, para o caso de estarem na mesma rede.</param>
public sealed record PeerRecord(
    NodeId NodeId,
    MacAddress Mac,
    byte Index,
    IPEndPoint? PublicEndpoint,
    IPEndPoint? LocalEndpoint)
{
    // Escrito literal em vez de `NodeId.Size + ...` porque `NodeId` aqui é também o nome
    // de uma propriedade de instância, e o color-color rule só confunde o leitor.
    public const int Size = 16 /*nodeId*/ + 6 /*mac*/ + 1 /*index*/ + 6 /*public*/ + 6 /*local*/; // 35

    public void WriteTo(ref SpanWriter writer)
    {
        writer.WriteNodeId(NodeId);
        writer.WriteMac(Mac);
        writer.WriteByte(Index);
        writer.WriteEndpoint(PublicEndpoint);
        writer.WriteEndpoint(LocalEndpoint);
    }

    public static bool TryRead(ref SpanReader reader, [NotNullWhen(true)] out PeerRecord? record)
    {
        record = null;

        if (!reader.TryReadNodeId(out var nodeId)) return false;
        if (!reader.TryReadMac(out var mac)) return false;
        if (!reader.TryReadByte(out byte index)) return false;
        if (!reader.TryReadEndpoint(out var pub)) return false;
        if (!reader.TryReadEndpoint(out var local)) return false;

        record = new PeerRecord(nodeId, mac, index, pub, local);
        return true;
    }

    /// <summary>Endpoints candidatos para hole punching, em ordem de preferência.</summary>
    public IEnumerable<IPEndPoint> CandidateEndpoints()
    {
        // Local primeiro: se estivermos na mesma LAN física, esse caminho é imediato
        // e evita depender de hairpin NAT, que muitos roteadores domésticos não fazem.
        if (LocalEndpoint is not null) yield return LocalEndpoint;
        if (PublicEndpoint is not null && !PublicEndpoint.Equals(LocalEndpoint)) yield return PublicEndpoint;
    }

    public override string ToString() =>
        $"{NodeId.ToShortString()} 25.0.0.{Index} [{Mac}] pub={PublicEndpoint?.ToString() ?? "-"} loc={LocalEndpoint?.ToString() ?? "-"}";
}
