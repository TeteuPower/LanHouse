using System.Buffers.Binary;
using System.Net;

namespace VirtualLan.Core.Protocol;

/// <summary>Escritor sequencial sobre um <see cref="Span{T}"/>. Lança se estourar o buffer.</summary>
public ref struct SpanWriter(Span<byte> buffer)
{
    private readonly Span<byte> _buffer = buffer;
    private int _position = 0;

    public readonly int Position => _position;
    public readonly int Remaining => _buffer.Length - _position;

    private Span<byte> Take(int count)
    {
        if (Remaining < count)
            throw new InvalidOperationException($"Buffer insuficiente: precisa de {count}, restam {Remaining}.");

        var slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    public void WriteByte(byte value) => Take(1)[0] = value;

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Take(2), value);

    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Take(value.Length));

    public void WriteNodeId(NodeId value) => value.WriteTo(Take(NodeId.Size));

    public void WriteNetworkId(NetworkId value) => value.WriteTo(Take(NetworkId.Size));

    public void WriteMac(Net.MacAddress value) => value.WriteTo(Take(Net.MacAddress.Size));

    /// <summary>Escreve um endpoint IPv4 como 4 bytes de endereço + 2 de porta.</summary>
    public void WriteEndpoint(IPEndPoint? value)
    {
        if (value is null || value.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            Take(6).Clear();
            return;
        }

        var dst = Take(6);
        if (!value.Address.TryWriteBytes(dst[..4], out int written) || written != 4)
            throw new ArgumentException("Endereço IPv4 inválido.", nameof(value));

        BinaryPrimitives.WriteUInt16BigEndian(dst[4..], (ushort)value.Port);
    }
}

/// <summary>Leitor sequencial sobre um <see cref="ReadOnlySpan{T}"/>. Retorna false em vez de lançar.</summary>
public ref struct SpanReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position = 0;

    public readonly int Remaining => _buffer.Length - _position;

    private bool TryTake(int count, out ReadOnlySpan<byte> slice)
    {
        if (Remaining < count)
        {
            slice = default;
            return false;
        }

        slice = _buffer.Slice(_position, count);
        _position += count;
        return true;
    }

    public bool TryReadByte(out byte value)
    {
        if (!TryTake(1, out var s)) { value = 0; return false; }
        value = s[0];
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (!TryTake(2, out var s)) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt16BigEndian(s);
        return true;
    }

    public bool TryReadBytes(int count, out ReadOnlySpan<byte> value) => TryTake(count, out value);

    public bool TryReadNodeId(out NodeId value)
    {
        if (!TryTake(NodeId.Size, out var s)) { value = default; return false; }
        value = new NodeId(s);
        return true;
    }

    public bool TryReadNetworkId(out NetworkId value)
    {
        if (!TryTake(NetworkId.Size, out var s)) { value = default; return false; }
        value = new NetworkId(s);
        return true;
    }

    public bool TryReadMac(out Net.MacAddress value)
    {
        if (!TryTake(Net.MacAddress.Size, out var s)) { value = default; return false; }
        value = new Net.MacAddress(s);
        return true;
    }

    /// <summary>Lê 4 bytes de IPv4 + 2 de porta. Endpoint todo-zero é traduzido para null.</summary>
    public bool TryReadEndpoint(out IPEndPoint? value)
    {
        value = null;
        if (!TryTake(6, out var s)) return false;

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(s[4..]);
        if (port == 0 && s[..4].IndexOfAnyExcept((byte)0) < 0) return true;

        value = new IPEndPoint(new IPAddress(s[..4]), port);
        return true;
    }

    /// <summary>Devolve o restante do buffer sem consumir mais nada.</summary>
    public ReadOnlySpan<byte> ReadRest()
    {
        var rest = _buffer[_position..];
        _position = _buffer.Length;
        return rest;
    }
}
