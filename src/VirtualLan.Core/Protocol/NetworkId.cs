using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace VirtualLan.Core.Protocol;

/// <summary>
/// Identificador de 128 bits de uma rede virtual, derivado de (nome, senha) via HKDF.
/// É a única coisa que o relay conhece sobre a rede — ele não consegue derivar a chave de dados.
/// </summary>
public readonly struct NetworkId : IEquatable<NetworkId>
{
    public const int Size = 16;

    private readonly ulong _hi;
    private readonly ulong _lo;

    public NetworkId(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"NetworkId precisa de {Size} bytes, recebeu {source.Length}.", nameof(source));

        _hi = BinaryPrimitives.ReadUInt64BigEndian(source);
        _lo = BinaryPrimitives.ReadUInt64BigEndian(source[8..]);
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Buffer precisa de {Size} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt64BigEndian(destination, _hi);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _lo);
    }

    public byte[] ToArray()
    {
        var b = new byte[Size];
        WriteTo(b);
        return b;
    }

    public bool Equals(NetworkId other) => _hi == other._hi && _lo == other._lo;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is NetworkId n && Equals(n);

    public override int GetHashCode() => HashCode.Combine(_hi, _lo);

    public override string ToString() => $"{_hi:x16}{_lo:x16}";

    public string ToShortString() => $"{_hi >> 40:x6}";

    public static bool operator ==(NetworkId a, NetworkId b) => a.Equals(b);
    public static bool operator !=(NetworkId a, NetworkId b) => !a.Equals(b);
}
