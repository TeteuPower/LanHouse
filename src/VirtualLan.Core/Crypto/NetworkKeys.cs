using System.Security.Cryptography;
using System.Text;
using VirtualLan.Core.Protocol;

namespace VirtualLan.Core.Crypto;

/// <summary>
/// Deriva, a partir de (nome da rede, senha), duas coisas independentes:
///
///   networkId = HKDF(ikm=senha, salt=nome, info="vlan/v1/network-id", 16 B)
///   dataKey   = HKDF(ikm=senha, salt=nome, info="vlan/v1/data-key",   32 B)
///
/// O relay recebe apenas o <c>networkId</c>. Como HKDF-Expand é uma PRF, conhecer a saída
/// para um <c>info</c> não dá nenhuma vantagem para recuperar a saída de outro <c>info</c>,
/// nem a IKM. Portanto o relay não consegue decifrar o tráfego que encaminha.
/// </summary>
public sealed class NetworkKeys
{
    private const string NetworkIdInfo = "vlan/v1/network-id";
    private const string DataKeyInfo = "vlan/v1/data-key";

    public NetworkId NetworkId { get; }

    /// <summary>Chave AES-256-GCM compartilhada por todos os membros da rede.</summary>
    public byte[] DataKey { get; }

    private NetworkKeys(NetworkId networkId, byte[] dataKey)
    {
        NetworkId = networkId;
        DataKey = dataKey;
    }

    public static NetworkKeys Derive(string networkName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);
        ArgumentNullException.ThrowIfNull(password);

        // Normaliza o nome para que "MinhaRede" e "minharede " gerem a mesma rede.
        byte[] salt = Encoding.UTF8.GetBytes(networkName.Trim().ToLowerInvariant());
        byte[] ikm = Encoding.UTF8.GetBytes(password);

        Span<byte> idBytes = stackalloc byte[NetworkId.Size];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, idBytes, salt, Encoding.UTF8.GetBytes(NetworkIdInfo));

        byte[] dataKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, dataKey, salt, Encoding.UTF8.GetBytes(DataKeyInfo));

        CryptographicOperations.ZeroMemory(ikm);

        return new NetworkKeys(new NetworkId(idBytes), dataKey);
    }
}
