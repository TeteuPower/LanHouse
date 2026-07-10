using System.Net;

namespace VirtualLan.Node;

public sealed class NodeOptions
{
    /// <summary>Host:porta do relay. Ex.: "vpn.meuservidor.com:7777".</summary>
    public required string RelayHost { get; init; }

    public required int RelayPort { get; init; }

    /// <summary>Nome da rede virtual. Junto com a senha, determina o networkId.</summary>
    public required string NetworkName { get; init; }

    public required string Password { get; init; }

    /// <summary>Nome do adaptador TAP. Se null, exige que exista exatamente um.</summary>
    public string? AdapterName { get; init; }

    /// <summary>Porta UDP local. 0 = efêmera (recomendado).</summary>
    public int LocalPort { get; init; }

    public IPAddress SubnetBase { get; init; } = IPAddress.Parse("25.0.0.0");
    public IPAddress SubnetMask { get; init; } = IPAddress.Parse("255.255.255.0");

    public const string FirewallRuleName = "VirtualLan (rede virtual 25.0.0.0/24)";

    public IPAddress AddressForIndex(byte index)
    {
        byte[] octets = SubnetBase.GetAddressBytes();
        octets[3] = index;
        return new IPAddress(octets);
    }
}
