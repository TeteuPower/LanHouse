namespace VirtualLan.Core.Net;

/// <summary>
/// Leitor de cabeçalho Ethernet II sem cópia. Só precisamos de dst/src/ethertype
/// para tomar a decisão de switching.
/// </summary>
public static class EthernetFrame
{
    /// <summary>Cabeçalho Ethernet II: 6 dst + 6 src + 2 ethertype.</summary>
    public const int HeaderSize = 14;

    /// <summary>Menor quadro Ethernet válido sem FCS (o TAP não entrega FCS).</summary>
    public const int MinSize = HeaderSize;

    /// <summary>Maior quadro que aceitamos: 1500 de payload + cabeçalho.</summary>
    public const int MaxSize = 1500 + HeaderSize;

    public static bool IsValid(ReadOnlySpan<byte> frame) => frame.Length is >= MinSize and <= MaxSize;

    public static MacAddress GetDestination(ReadOnlySpan<byte> frame) => new(frame[..6]);

    public static MacAddress GetSource(ReadOnlySpan<byte> frame) => new(frame.Slice(6, 6));

    public static ushort GetEtherType(ReadOnlySpan<byte> frame) => (ushort)((frame[12] << 8) | frame[13]);

    public static string DescribeEtherType(ushort etherType) => etherType switch
    {
        0x0800 => "IPv4",
        0x0806 => "ARP",
        0x86DD => "IPv6",
        0x8100 => "802.1Q",
        _ => $"0x{etherType:x4}",
    };
}
