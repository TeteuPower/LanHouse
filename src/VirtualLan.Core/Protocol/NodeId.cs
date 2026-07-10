using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace VirtualLan.Core.Protocol;

/// <summary>
/// Identificador de 128 bits de um nó, gerado aleatoriamente a cada execução do processo.
///
/// IMPORTANTE: a natureza efêmera é um requisito de segurança, não um descuido.
/// Os 4 primeiros bytes formam o prefixo do nonce AES-GCM (ver <see cref="Crypto.NonceGenerator"/>).
/// Se o NodeId fosse persistido, um restart reiniciaria o contador e reusaria nonces
/// sob a mesma chave — quebra total de AES-GCM.
/// </summary>
public readonly struct NodeId : IEquatable<NodeId>
{
    public const int Size = 16;

    public static readonly NodeId Zero = default;

    private readonly ulong _hi;
    private readonly ulong _lo;

    private NodeId(ulong hi, ulong lo) => (_hi, _lo) = (hi, lo);

    public NodeId(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"NodeId precisa de {Size} bytes, recebeu {source.Length}.", nameof(source));

        _hi = BinaryPrimitives.ReadUInt64BigEndian(source);
        _lo = BinaryPrimitives.ReadUInt64BigEndian(source[8..]);
    }

    public bool IsZero => _hi == 0 && _lo == 0;

    public static NodeId CreateRandom()
    {
        Span<byte> b = stackalloc byte[Size];
        RandomNumberGenerator.Fill(b);
        return new NodeId(b);
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Buffer precisa de {Size} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt64BigEndian(destination, _hi);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _lo);
    }

    /// <summary>Os 4 bytes mais significativos, usados como prefixo do nonce.</summary>
    public uint NoncePrefix => (uint)(_hi >> 32);

    public byte[] ToArray()
    {
        var b = new byte[Size];
        WriteTo(b);
        return b;
    }

    public bool Equals(NodeId other) => _hi == other._hi && _lo == other._lo;

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is NodeId n && Equals(n);

    public override int GetHashCode() => HashCode.Combine(_hi, _lo);

    public override string ToString() => $"{_hi:x16}{_lo:x16}";

    /// <summary>Forma curta para logs.</summary>
    public string ToShortString() => $"{_hi >> 32:x8}";

    public static bool operator ==(NodeId a, NodeId b) => a.Equals(b);
    public static bool operator !=(NodeId a, NodeId b) => !a.Equals(b);
}
