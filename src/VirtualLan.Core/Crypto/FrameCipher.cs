using System.Security.Cryptography;
using VirtualLan.Core.Protocol;

namespace VirtualLan.Core.Crypto;

/// <summary>
/// Cifra e decifra quadros Ethernet com AES-256-GCM.
///
/// Layout da região selada, contígua no pacote:
///
///   [ nonce (12) ][ ciphertext (n) ][ tag (16) ]
///
/// O AAD é sempre o prefixo em claro do pacote (cabeçalho + ids), de forma que qualquer
/// adulteração de <c>type</c>, <c>networkId</c>, <c>srcNodeId</c> ou <c>dstNodeId</c> por um
/// relay malicioso invalide a tag.
/// </summary>
public sealed class FrameCipher : IDisposable
{
    public const int NonceSize = NonceGenerator.NonceSize; // 12
    public const int TagSize = 16;

    /// <summary>Bytes que a selagem acrescenta ao texto claro.</summary>
    public const int Overhead = NonceSize + TagSize; // 28

    private readonly AesGcm _aes;
    private readonly NonceGenerator _nonces;

    // AesGcm não é documentado como thread-safe. O caminho quente aqui é da ordem de
    // alguns milhares de pacotes/s; a contenção do lock é irrelevante frente ao custo do syscall.
    private readonly object _encryptLock = new();
    private readonly object _decryptLock = new();

    public FrameCipher(NetworkKeys keys, NodeId localNodeId)
    {
        ArgumentNullException.ThrowIfNull(keys);

        _aes = new AesGcm(keys.DataKey, TagSize);
        _nonces = new NonceGenerator(localNodeId);
    }

    /// <summary>
    /// Sela <paramref name="plaintext"/> dentro de <paramref name="packet"/> a partir de
    /// <paramref name="payloadOffset"/>. O AAD é <c>packet[..payloadOffset]</c>, que já deve
    /// estar totalmente escrito.
    /// </summary>
    /// <returns>Tamanho total do pacote (payloadOffset + 12 + n + 16).</returns>
    public int Seal(Span<byte> packet, int payloadOffset, ReadOnlySpan<byte> plaintext)
    {
        int total = payloadOffset + Overhead + plaintext.Length;
        if (packet.Length < total)
            throw new ArgumentException($"Pacote precisa de {total} bytes, tem {packet.Length}.", nameof(packet));

        ReadOnlySpan<byte> aad = packet[..payloadOffset];
        Span<byte> nonce = packet.Slice(payloadOffset, NonceSize);
        Span<byte> ciphertext = packet.Slice(payloadOffset + NonceSize, plaintext.Length);
        Span<byte> tag = packet.Slice(payloadOffset + NonceSize + plaintext.Length, TagSize);

        _nonces.Next(nonce);

        lock (_encryptLock)
        {
            _aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        return total;
    }

    /// <summary>
    /// Abre a região selada de <paramref name="packet"/> começando em <paramref name="payloadOffset"/>.
    /// Retorna false se a tag não bater (pacote adulterado, chave errada ou lixo da rede) —
    /// nunca lança, porque isso é entrada não-confiável e um atacante não deve conseguir
    /// nos custar uma exceção por pacote.
    /// </summary>
    public bool TryOpen(ReadOnlySpan<byte> packet, int payloadOffset, Span<byte> plaintext, out int plaintextLength)
    {
        plaintextLength = 0;

        int sealedLength = packet.Length - payloadOffset;
        if (sealedLength < Overhead) return false;

        int ptLength = sealedLength - Overhead;
        if (plaintext.Length < ptLength) return false;

        ReadOnlySpan<byte> aad = packet[..payloadOffset];
        ReadOnlySpan<byte> nonce = packet.Slice(payloadOffset, NonceSize);
        ReadOnlySpan<byte> ciphertext = packet.Slice(payloadOffset + NonceSize, ptLength);
        ReadOnlySpan<byte> tag = packet.Slice(payloadOffset + NonceSize + ptLength, TagSize);

        try
        {
            lock (_decryptLock)
            {
                _aes.Decrypt(nonce, ciphertext, tag, plaintext[..ptLength], aad);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }

        plaintextLength = ptLength;
        return true;
    }

    public void Dispose() => _aes.Dispose();
}
