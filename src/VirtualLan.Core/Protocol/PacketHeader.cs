namespace VirtualLan.Core.Protocol;

/// <summary>
/// Cabeçalho comum de 8 bytes, sempre em claro:
/// magic(4) | version(1) | type(1) | flags(1) | reserved(1)
/// </summary>
public static class PacketHeader
{
    public const int Size = 8;
    public const byte Version = 1;

    /// <summary>"VLAN" em ASCII.</summary>
    private static ReadOnlySpan<byte> Magic => "VLAN"u8;

    public static void Write(Span<byte> destination, PacketType type)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Buffer precisa de {Size} bytes.", nameof(destination));

        Magic.CopyTo(destination);
        destination[4] = Version;
        destination[5] = (byte)type;
        destination[6] = 0; // flags
        destination[7] = 0; // reserved
    }

    /// <summary>
    /// Valida magic e versão e extrai o tipo. Não valida se o tipo é conhecido —
    /// isso é responsabilidade do dispatcher, que pode querer ignorar tipos futuros.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out PacketType type)
    {
        type = default;

        if (source.Length < Size) return false;
        if (!source[..4].SequenceEqual(Magic)) return false;
        if (source[4] != Version) return false;

        type = (PacketType)source[5];
        return true;
    }

    /// <summary>Versão do pacote, para poder responder <see cref="ErrorCode.ProtocolVersionMismatch"/>.</summary>
    public static bool TryPeekVersion(ReadOnlySpan<byte> source, out byte version)
    {
        version = 0;
        if (source.Length < Size || !source[..4].SequenceEqual(Magic)) return false;
        version = source[4];
        return true;
    }
}
