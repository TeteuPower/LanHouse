using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace VirtualLan.Node.Tap;

/// <summary>Um adaptador TAP instalado, localizado no registro do Windows.</summary>
/// <param name="InstanceId">GUID do NetCfgInstanceId, usado para montar o caminho do device.</param>
/// <param name="ComponentId">Como está no registro, ex.: "tap0901" ou "root\tap0901".</param>
/// <param name="Name">Nome amigável ("VirtualLan"), o mesmo que o netsh usa.</param>
public sealed record TapAdapterInfo(string InstanceId, string ComponentId, string Name)
{
    /// <summary>Caminho do device no namespace Win32. O sufixo ".tap" é exigido pelo driver.</summary>
    public string DevicePath => $@"\\.\Global\{InstanceId}.tap";

    public override string ToString() => $"{Name} ({ComponentId}, {InstanceId})";
}

/// <summary>Uma entrada qualquer da classe de rede, para diagnóstico quando nada casa.</summary>
public sealed record NetworkClassEntry(string SubKey, string ComponentId, string? InstanceId, string? Name);

/// <summary>
/// Localiza adaptadores tap-windows6 varrendo a classe de dispositivos de rede no registro.
///
/// Não usamos SetupAPI aqui de propósito: o registro é estável, documentado, e evita
/// centenas de linhas de P/Invoke para o mesmo resultado.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TapAdapterLocator
{
    /// <summary>GUID da classe de dispositivos "Net".</summary>
    private const string NetworkAdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    private const string NetworkConnectionsKey =
        @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    /// <summary>
    /// ComponentIds aceitos, já normalizados (sem o prefixo do enumerador).
    /// tap0901 = tap-windows6 (o que queremos); tap0801 = legado; tapoas = OpenVPN Connect.
    /// </summary>
    private static readonly string[] SupportedComponentIds = ["tap0901", "tap0801", "tapoas"];

    /// <summary>
    /// Drivers que aparecem na mesma classe mas NÃO servem: são Camada 3 e não transportam
    /// broadcast Ethernet, então nenhum jogo enxergaria a sala.
    /// </summary>
    private static readonly string[] KnownLayer3ComponentIds = ["wintun", "ovpn-dco", "wireguard"];

    /// <summary>
    /// Remove o prefixo do enumerador. O mesmo driver aparece como "tap0901" (instalador do
    /// OpenVPN) e como "root\tap0901" (criado pelo "tapctl create"). Comparar a string crua
    /// encontra um caso e perde o outro — foi exatamente esse o bug.
    /// </summary>
    public static string NormalizeComponentId(string componentId)
    {
        int separator = componentId.LastIndexOf('\\');
        return separator >= 0 ? componentId[(separator + 1)..] : componentId;
    }

    public static bool IsSupportedComponentId(string componentId) =>
        SupportedComponentIds.Contains(NormalizeComponentId(componentId), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TapAdapterInfo> FindAll()
    {
        List<TapAdapterInfo> adapters = [];

        foreach (var entry in EnumerateNetworkClass())
        {
            if (!IsSupportedComponentId(entry.ComponentId)) continue;
            if (entry.InstanceId is null) continue;

            adapters.Add(new TapAdapterInfo(entry.InstanceId, entry.ComponentId, entry.Name ?? entry.InstanceId));
        }

        return adapters;
    }

    /// <summary>Toda a classe de rede, sem filtro. Usado para explicar por que nada casou.</summary>
    public static IReadOnlyList<NetworkClassEntry> EnumerateNetworkClass()
    {
        List<NetworkClassEntry> entries = [];

        using var classKey = Registry.LocalMachine.OpenSubKey(NetworkAdapterClassKey);
        if (classKey is null) return entries;

        foreach (string subKeyName in classKey.GetSubKeyNames())
        {
            if (subKeyName.Length != 4 || !subKeyName.All(char.IsAsciiDigit)) continue;

            using var deviceKey = classKey.OpenSubKey(subKeyName);
            if (deviceKey?.GetValue("ComponentId") is not string componentId) continue;

            string? instanceId = deviceKey.GetValue("NetCfgInstanceId") as string;
            string? name = instanceId is null ? null : ResolveConnectionName(instanceId);

            entries.Add(new NetworkClassEntry(subKeyName, componentId, instanceId, name));
        }

        return entries;
    }

    /// <summary>
    /// Escolhe o adaptador a usar. Se <paramref name="preferredName"/> vier preenchido,
    /// exige aquele adaptador; caso contrário pega o único existente.
    /// </summary>
    public static TapAdapterInfo Select(string? preferredName)
    {
        var adapters = FindAll();

        if (adapters.Count == 0) throw new InvalidOperationException(BuildNotFoundMessage());

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var match = adapters.FirstOrDefault(a => string.Equals(a.Name, preferredName, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new InvalidOperationException(
                $"Adaptador TAP '{preferredName}' não encontrado. Disponíveis: {string.Join(", ", adapters.Select(a => a.Name))}");
        }

        if (adapters.Count > 1)
        {
            throw new InvalidOperationException(
                $"Há {adapters.Count} adaptadores TAP. Escolha um com --adapter \"<nome>\". " +
                $"Disponíveis: {string.Join(", ", adapters.Select(a => a.Name))}");
        }

        return adapters[0];
    }

    /// <summary>
    /// Mensagem de erro que mostra o que existe no registro em vez de só dizer "não achei".
    /// Sem isso, um ComponentId inesperado vira meia hora de depuração às cegas.
    /// </summary>
    private static string BuildNotFoundMessage()
    {
        var all = EnumerateNetworkClass();
        var layer3 = all.Where(e => KnownLayer3ComponentIds.Contains(NormalizeComponentId(e.ComponentId), StringComparer.OrdinalIgnoreCase)).ToList();

        var message = new StringBuilder();
        message.AppendLine("Nenhum adaptador TAP compatível encontrado.");
        message.AppendLine();
        message.AppendLine($"Procurei por ComponentId em [{string.Join(", ", SupportedComponentIds)}] " +
                           "(com ou sem prefixo de enumerador, ex.: 'root\\tap0901').");

        if (layer3.Count > 0)
        {
            message.AppendLine();
            message.AppendLine("Encontrei adaptadores de Camada 3, que NAO servem — eles nao transportam");
            message.AppendLine("broadcast Ethernet, entao o jogo nunca enxergaria a sala:");
            foreach (var e in layer3) message.AppendLine($"  {e.ComponentId,-24} {e.Name ?? e.InstanceId}");
        }

        message.AppendLine();
        message.AppendLine($"Adaptadores de rede presentes no registro ({all.Count}):");
        foreach (var e in all) message.AppendLine($"  [{e.SubKey}] {e.ComponentId,-24} {e.Name ?? "(sem nome)"}");

        message.AppendLine();
        message.AppendLine(@"Rode scripts\install-tap.ps1 como administrador.");

        return message.ToString();
    }

    private static string? ResolveConnectionName(string instanceId)
    {
        using var connectionKey = Registry.LocalMachine.OpenSubKey($@"{NetworkConnectionsKey}\{instanceId}\Connection");
        return connectionKey?.GetValue("Name") as string;
    }
}
