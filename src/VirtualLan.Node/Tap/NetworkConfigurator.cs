using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using VirtualLan.Core.Diagnostics;

namespace VirtualLan.Node.Tap;

/// <summary>
/// Configura o adaptador TAP via <c>netsh</c>.
///
/// Poderia ser feito com a IP Helper API (CreateUnicastIpAddressEntry etc.), mas netsh é
/// estável, auditável pelo usuário e não exige mais 300 linhas de P/Invoke. O custo é um
/// processo filho por comando, na inicialização apenas.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NetworkConfigurator
{
    /// <summary>
    /// 1400 = 1500 (MTU do caminho) - 80 (IP+UDP 28, header 8, srcNodeId 16, nonce 12, tag 16).
    /// A folga de 20 bytes cobre PPPoE, comum em ADSL/fibra residencial no Brasil.
    /// </summary>
    public const int Mtu = 1400;

    public static void ConfigureInterface(string adapterName, IPAddress address, IPAddress mask)
    {
        Log.Info($"Configurando '{adapterName}' → {address}/{MaskToPrefix(mask)} mtu={Mtu}");

        Run("netsh", $"interface ip set address name=\"{adapterName}\" static {address} {mask}");
        Run("netsh", $"interface ipv4 set subinterface \"{adapterName}\" mtu={Mtu} store=persistent");

        // Métrica baixa faz o Windows preferir a TAP ao escolher a interface de saída para
        // o broadcast limitado 255.255.255.255, que é como vários jogos anunciam a partida.
        // Não afeta a rota default: a TAP não tem gateway.
        Run("netsh", $"interface ipv4 set interface \"{adapterName}\" metric=1", ignoreFailure: true);
    }

    /// <summary>
    /// Marca a rede da TAP como "Privada". No perfil "Pública" o Firewall do Windows bloqueia
    /// a descoberta e o tráfego de entrada dos jogos, e o sintoma é exatamente o que o usuário
    /// mais odeia: ping funciona, sala não aparece.
    /// </summary>
    public static void TrySetPrivateProfile(string adapterName)
    {
        int exit = Run(
            "powershell",
            $"-NoProfile -NonInteractive -Command \"Set-NetConnectionProfile -InterfaceAlias '{adapterName}' -NetworkCategory Private\"",
            ignoreFailure: true);

        if (exit != 0)
            Log.Warn($"Não foi possível marcar '{adapterName}' como rede Privada. Faça manualmente se o jogo não enxergar a sala.");
    }

    /// <summary>Regra de firewall permitindo todo tráfego de entrada vindo da sub-rede virtual.</summary>
    public static void EnsureFirewallRule(string ruleName, IPAddress subnet, IPAddress mask)
    {
        string scope = $"{subnet}/{MaskToPrefix(mask)}";

        Run("netsh", $"advfirewall firewall delete rule name=\"{ruleName}\"", ignoreFailure: true);
        Run("netsh",
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow " +
            $"protocol=any remoteip={scope} profile=any",
            ignoreFailure: true);

        Log.Info($"Regra de firewall '{ruleName}' aplicada para {scope}");
    }

    public static void RemoveFirewallRule(string ruleName)
        => Run("netsh", $"advfirewall firewall delete rule name=\"{ruleName}\"", ignoreFailure: true);

    public static int MaskToPrefix(IPAddress mask)
    {
        int bits = 0;
        foreach (byte b in mask.GetAddressBytes()) bits += System.Numerics.BitOperations.PopCount(b);
        return bits;
    }

    private static int Run(string fileName, string arguments, bool ignoreFailure = false)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Não foi possível iniciar '{fileName}'.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string message = $"{fileName} {arguments} → exit {process.ExitCode}. {stdout.Trim()} {stderr.Trim()}".Trim();

            if (ignoreFailure) Log.Debug(message);
            else throw new InvalidOperationException(message);
        }

        return process.ExitCode;
    }
}
