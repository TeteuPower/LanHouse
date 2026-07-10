using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace VirtualLan.Core.Net;

/// <summary>
/// Endereço MAC de 48 bits. Struct imutável, comparável e hasheável sem alocar,
/// para poder ser chave de dicionário no caminho quente de forwarding.
/// </summary>
public readonly struct MacAddress : IEquatable<MacAddress>
{
    public const int Size = 6;

    public static readonly MacAddress Broadcast = new(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
    public static readonly MacAddress Zero = default;

    private readonly byte _b0, _b1, _b2, _b3, _b4, _b5;

    public MacAddress(byte b0, byte b1, byte b2, byte b3, byte b4, byte b5)
        => (_b0, _b1, _b2, _b3, _b4, _b5) = (b0, b1, b2, b3, b4, b5);

    public MacAddress(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"MAC precisa de {Size} bytes, recebeu {source.Length}.", nameof(source));

        _b0 = source[0]; _b1 = source[1]; _b2 = source[2];
        _b3 = source[3]; _b4 = source[4]; _b5 = source[5];
    }

    /// <summary>Bit menos significativo do primeiro octeto: 1 = multicast/broadcast.</summary>
    public bool IsGroupAddress => (_b0 & 0x01) != 0;

    public bool IsBroadcast => Equals(Broadcast);

    public bool IsZero => Equals(Zero);

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Buffer precisa de {Size} bytes.", nameof(destination));

        destination[0] = _b0; destination[1] = _b1; destination[2] = _b2;
        destination[3] = _b3; destination[4] = _b4; destination[5] = _b5;
    }

    public byte[] ToArray() => [_b0, _b1, _b2, _b3, _b4, _b5];

    /// <summary>
    /// Gera um MAC aleatório "locally administered, unicast" (bit U/L = 1, bit I/G = 0),
    /// o que garante que nunca colide com um MAC de fabricante real.
    /// </summary>
    public static MacAddress CreateRandomLocal()
    {
        Span<byte> b = stackalloc byte[Size];
        RandomNumberGenerator.Fill(b);
        b[0] = (byte)((b[0] & 0xFE) | 0x02);
        return new MacAddress(b);
    }

    public static bool TryParse(string? text, out MacAddress mac)
    {
        mac = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split([':', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != Size) return false;

        Span<byte> b = stackalloc byte[Size];
        for (int i = 0; i < Size; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out b[i]))
                return false;
        }

        mac = new MacAddress(b);
        return true;
    }

    public static MacAddress Parse(string text) =>
        TryParse(text, out var mac) ? mac : throw new FormatException($"MAC inválido: '{text}'.");

    public bool Equals(MacAddress other) =>
        _b0 == other._b0 && _b1 == other._b1 && _b2 == other._b2 &&
        _b3 == other._b3 && _b4 == other._b4 && _b5 == other._b5;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is MacAddress m && Equals(m);

    public override int GetHashCode()
    {
        // FNV-1a de 6 bytes. Barato e bem distribuído.
        uint h = 2166136261u;
        h = (h ^ _b0) * 16777619u;
        h = (h ^ _b1) * 16777619u;
        h = (h ^ _b2) * 16777619u;
        h = (h ^ _b3) * 16777619u;
        h = (h ^ _b4) * 16777619u;
        h = (h ^ _b5) * 16777619u;
        return (int)h;
    }

    public override string ToString() => $"{_b0:x2}:{_b1:x2}:{_b2:x2}:{_b3:x2}:{_b4:x2}:{_b5:x2}";

    public static bool operator ==(MacAddress a, MacAddress b) => a.Equals(b);
    public static bool operator !=(MacAddress a, MacAddress b) => !a.Equals(b);
}
