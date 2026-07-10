using VirtualLan.Node.Peers;

namespace VirtualLan.Node;

/// <summary>Estágios de conexão do nó, para a GUI refletir o que está acontecendo.</summary>
public enum NodeConnectionState
{
    /// <summary>Ainda não começou.</summary>
    Idle,

    /// <summary>Resolvendo o relay, abrindo o adaptador e ligando os laços.</summary>
    Resolving,

    /// <summary>Registrando no relay (ainda sem IP virtual atribuído).</summary>
    Connecting,

    /// <summary>Registrado: o adaptador tem IP virtual e a rede está operacional.</summary>
    Connected,

    /// <summary>Parou por erro. O detalhe acompanha o evento.</summary>
    Faulted,
}

/// <summary>
/// Fotografia imutável de um peer para a GUI exibir, sem expor os campos mutáveis internos
/// (que são acessados de vários laços). Gerada sob demanda por <see cref="NodeService.SnapshotPeers"/>.
/// </summary>
/// <param name="Index">Índice atribuído pelo relay (o último octeto do IP virtual).</param>
/// <param name="VirtualIp">IP virtual, ex.: "25.0.0.2".</param>
/// <param name="Mac">MAC do adaptador do peer.</param>
/// <param name="NodeIdShort">Identificador curto do nó, para diagnóstico.</param>
/// <param name="Path">Se o tráfego vai direto (P2P) ou pelo relay.</param>
/// <param name="PathDetail">Endpoint do caminho direto, ou "via relay".</param>
public sealed record PeerView(
    byte Index,
    string VirtualIp,
    string Mac,
    string NodeIdShort,
    PathState Path,
    string PathDetail);
