using System.Buffers.Binary;
using VirtualLan.Core.Protocol;

namespace VirtualLan.Core.Crypto;

/// <summary>
/// Produz nonces de 96 bits para AES-GCM, garantindo unicidade sob uma mesma chave.
///
///   nonce = nodeId[0..4] ‖ counter(8, big-endian)
///
/// Unicidade entre nós:    o prefixo vem do NodeId, aleatório de 128 bits.
/// Unicidade entre sessões: o NodeId é regerado a cada execução do processo.
/// Unicidade dentro da sessão: contador monotônico de 64 bits (nunca dá a volta na prática).
///
/// Reusar um nonce sob a mesma chave em AES-GCM revela o XOR dos textos claros E permite
/// forjar tags arbitrárias. Este tipo existe para tornar isso estruturalmente impossível.
/// </summary>
public sealed class NonceGenerator(NodeId nodeId)
{
    public const int NonceSize = 12;

    private readonly uint _prefix = nodeId.NoncePrefix;
    private ulong _counter;

    public void Next(Span<byte> destination)
    {
        if (destination.Length < NonceSize)
            throw new ArgumentException($"Nonce precisa de {NonceSize} bytes.", nameof(destination));

        ulong counter = Interlocked.Increment(ref _counter);

        BinaryPrimitives.WriteUInt32BigEndian(destination, _prefix);
        BinaryPrimitives.WriteUInt64BigEndian(destination[4..], counter);
    }
}
