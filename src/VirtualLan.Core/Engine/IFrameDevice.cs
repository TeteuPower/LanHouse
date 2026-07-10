using System.Net;
using VirtualLan.Core.Net;

namespace VirtualLan.Core.Engine;

/// <summary>
/// Um dispositivo capaz de ler e escrever quadros Ethernet crus.
///
/// Existe para que o motor não conheça o driver TAP do Windows diretamente. Em produção, a
/// implementação é o adaptador tap-windows6 (ver VirtualLan.Node.Tap.TapDevice). Para teste,
/// pode ser um TAP do Linux (/dev/net/tun) ou um dispositivo sintético em memória.
///
/// CONTRATO: cada ReadFrameAsync devolve exatamente um quadro Ethernet; cada WriteFrameAsync
/// injeta exatamente um. Sem framing adicional, sem FCS.
///
/// NOTA (etapa #7 do HANDOFF): o motor ainda vive em VirtualLan.Node.NodeService. A tarefa é
/// movê-lo para cá como NodeEngine, recebendo IFrameDevice e INetworkConfigurator por injeção.
/// Estes contratos já estão prontos para isso.
/// </summary>
public interface IFrameDevice : IDisposable
{
    /// <summary>Nome amigável da interface (ex.: "VirtualLan"), usado na configuração de IP.</summary>
    string Name { get; }

    /// <summary>MAC atribuído pelo driver ao adaptador. É a identidade L2 deste nó.</summary>
    MacAddress MacAddress { get; }

    /// <summary>Lê um quadro Ethernet. Bloqueia (assincronamente) até haver um.</summary>
    ValueTask<int> ReadFrameAsync(Memory<byte> destination, CancellationToken cancellationToken);

    /// <summary>Injeta um quadro Ethernet na pilha do sistema operacional.</summary>
    ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);
}

/// <summary>
/// Configura o endereçamento IP do dispositivo virtual e, no Windows, o firewall/perfil de rede.
///
/// Em produção é implementado por VirtualLan.Node.Tap.NetworkConfigurator (netsh). Para teste em
/// Linux, uma implementação com "ip addr add" / "ip link set". Métodos com corpo default são
/// no-ops para que a implementação de teste só precise sobrescrever ConfigureInterface.
/// </summary>
public interface INetworkConfigurator
{
    /// <summary>MTU aplicado à interface. 1400 cobre o overhead do VirtualLan + folga PPPoE.</summary>
    int Mtu => 1400;

    /// <summary>Atribui IP e máscara à interface e ajusta o MTU.</summary>
    void ConfigureInterface(string adapterName, IPAddress address, IPAddress mask);

    /// <summary>
    /// No Windows: marca a rede como Privada e abre o firewall para a sub-rede virtual, para que
    /// a descoberta de sala dos jogos funcione. No-op nas demais plataformas.
    /// </summary>
    void ApplyHostFirewallAndProfile(string adapterName, IPAddress subnet, IPAddress mask) { }

    /// <summary>Desfaz o que ApplyHostFirewallAndProfile criou (regra de firewall). No-op por padrão.</summary>
    void Cleanup(string adapterName) { }
}
