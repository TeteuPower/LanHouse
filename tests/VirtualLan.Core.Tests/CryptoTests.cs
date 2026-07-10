using VirtualLan.Core.Crypto;
using VirtualLan.Core.Protocol;
using Xunit;

namespace VirtualLan.Core.Tests;

public class CryptoTests
{
    [Fact]
    public void SameNameAndPassword_ProduceSameKeys()
    {
        var a = NetworkKeys.Derive("dota-sexta", "senha forte");
        var b = NetworkKeys.Derive("  DOTA-SEXTA  ", "senha forte"); // normalização: trim + lowercase

        Assert.Equal(a.NetworkId, b.NetworkId);
        Assert.Equal(a.DataKey, b.DataKey);
    }

    [Fact]
    public void DifferentPassword_ProducesDifferentNetworkId()
    {
        var a = NetworkKeys.Derive("rede", "senha1");
        var b = NetworkKeys.Derive("rede", "senha2");

        Assert.NotEqual(a.NetworkId, b.NetworkId);
        Assert.NotEqual(a.DataKey, b.DataKey);
    }

    [Fact]
    public void NetworkId_DoesNotLeakDataKey()
    {
        var keys = NetworkKeys.Derive("rede", "senha");

        // Propriedade que sustenta o modelo de confiança: o relay conhece o networkId e
        // isso não pode coincidir com nenhum prefixo da chave de dados.
        Assert.NotEqual(keys.NetworkId.ToArray(), keys.DataKey[..16]);
    }

    [Fact]
    public void Seal_ThenOpen_RecoversFrame()
    {
        var keys = NetworkKeys.Derive("rede", "senha");
        var nodeId = NodeId.CreateRandom();

        using var sender = new FrameCipher(keys, nodeId);
        using var receiver = new FrameCipher(keys, NodeId.CreateRandom());

        byte[] frame = [.. Enumerable.Range(0, 500).Select(i => (byte)i)];
        byte[] packet = new byte[Wire.MaxPacketSize];

        Wire.WriteDataDirectHeader(packet, nodeId);
        int length = sender.Seal(packet, Wire.DataDirectPayloadOffset, frame);

        Assert.Equal(Wire.DataDirectPayloadOffset + FrameCipher.Overhead + frame.Length, length);

        byte[] recovered = new byte[Wire.MaxFrameSize];
        Assert.True(receiver.TryOpen(packet.AsSpan(0, length), Wire.DataDirectPayloadOffset, recovered, out int recoveredLength));

        Assert.Equal(frame.Length, recoveredLength);
        Assert.Equal(frame, recovered[..recoveredLength]);
    }

    [Fact]
    public void TamperedCiphertext_FailsToOpen()
    {
        var keys = NetworkKeys.Derive("rede", "senha");
        using var cipher = new FrameCipher(keys, NodeId.CreateRandom());

        byte[] frame = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14];
        byte[] packet = new byte[Wire.MaxPacketSize];

        Wire.WriteDataDirectHeader(packet, NodeId.CreateRandom());
        int length = cipher.Seal(packet, Wire.DataDirectPayloadOffset, frame);

        packet[Wire.DataDirectPayloadOffset + FrameCipher.NonceSize] ^= 0x01; // 1 bit do ciphertext

        byte[] recovered = new byte[Wire.MaxFrameSize];
        Assert.False(cipher.TryOpen(packet.AsSpan(0, length), Wire.DataDirectPayloadOffset, recovered, out _));
    }

    [Fact]
    public void TamperedAad_FailsToOpen()
    {
        var keys = NetworkKeys.Derive("rede", "senha");
        using var cipher = new FrameCipher(keys, NodeId.CreateRandom());

        byte[] frame = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14];
        byte[] packet = new byte[Wire.MaxPacketSize];

        Wire.WriteDataRelayHeader(packet, keys.NetworkId, NodeId.CreateRandom(), NodeId.Zero);
        int length = cipher.Seal(packet, Wire.DataRelayPayloadOffset, frame);

        // Um relay malicioso trocando o destino do broadcast para um nó específico.
        packet[Wire.DataRelayPayloadOffset - 1] ^= 0xFF;

        byte[] recovered = new byte[Wire.MaxFrameSize];
        Assert.False(cipher.TryOpen(packet.AsSpan(0, length), Wire.DataRelayPayloadOffset, recovered, out _));
    }

    [Fact]
    public void WrongPassword_FailsToOpen()
    {
        var sealer = new FrameCipher(NetworkKeys.Derive("rede", "certa"), NodeId.CreateRandom());
        var opener = new FrameCipher(NetworkKeys.Derive("rede", "errada"), NodeId.CreateRandom());

        using (sealer)
        using (opener)
        {
            byte[] frame = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14];
            byte[] packet = new byte[Wire.MaxPacketSize];

            Wire.WriteDataDirectHeader(packet, NodeId.CreateRandom());
            int length = sealer.Seal(packet, Wire.DataDirectPayloadOffset, frame);

            byte[] recovered = new byte[Wire.MaxFrameSize];
            Assert.False(opener.TryOpen(packet.AsSpan(0, length), Wire.DataDirectPayloadOffset, recovered, out _));
        }
    }

    [Fact]
    public void Nonces_NeverRepeatWithinASession()
    {
        var nodeId = NodeId.CreateRandom();
        var generator = new NonceGenerator(nodeId);

        HashSet<string> seen = [];
        Span<byte> nonce = stackalloc byte[NonceGenerator.NonceSize];

        for (int i = 0; i < 10_000; i++)
        {
            generator.Next(nonce);
            Assert.True(seen.Add(Convert.ToHexString(nonce)), "nonce repetido — quebraria AES-GCM");
        }
    }

    [Fact]
    public void Nonces_OfDifferentNodes_HaveDifferentPrefixes()
    {
        var a = NodeId.CreateRandom();
        var b = NodeId.CreateRandom();

        Assert.NotEqual(a.NoncePrefix, b.NoncePrefix);
    }
}
