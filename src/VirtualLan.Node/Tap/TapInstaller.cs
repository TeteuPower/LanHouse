using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using VirtualLan.Core.Diagnostics;

namespace VirtualLan.Node.Tap;

/// <summary>
/// Garante que exista um adaptador tap-windows6 pronto para uso, sem o usuário rodar PowerShell.
///
/// É a versão em C# de <c>scripts/install-tap.ps1</c>, chamada pela GUI na primeira execução:
/// detecta a ausência do driver, obtém o instalador oficial da OpenVPN (arquivo local ao lado do
/// app ou download em runtime), instala em modo silencioso, cria o adaptador e o renomeia.
///
/// Não distribuímos o driver no repositório: ele é GPL-2.0 da OpenVPN Inc. O usuário pode, se
/// quiser um pacote offline, colocar um <c>tap-windows*.exe</c> ao lado do executável — o
/// instalador usa esse arquivo em vez de baixar.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TapInstaller
{
    public const string DefaultAdapterName = "VirtualLan";

    /// <summary>Instalador oficial e assinado do tap-windows6, publicado pela OpenVPN.</summary>
    private const string InstallerUrl =
        "https://build.openvpn.net/downloads/releases/latest/tap-windows-latest-stable.exe";

    /// <summary>true se já existe um adaptador compatível com o nome pedido (ou qualquer um, se name for null).</summary>
    public static bool AdapterExists(string? name)
    {
        var adapters = TapAdapterLocator.FindAll();
        return string.IsNullOrWhiteSpace(name)
            ? adapters.Count > 0
            : adapters.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Deixa um adaptador chamado <paramref name="adapterName"/> pronto. Idempotente.
    /// Relata cada passo por <paramref name="progress"/> para a GUI mostrar. Requer elevação.
    /// </summary>
    public static async Task EnsureInstalledAsync(
        string adapterName, IProgress<string>? progress, CancellationToken ct)
    {
        Report(progress, "Verificando adaptadores TAP já instalados...");

        var adapters = TapAdapterLocator.FindAll();
        if (adapters.Any(a => string.Equals(a.Name, adapterName, StringComparison.OrdinalIgnoreCase)))
        {
            Report(progress, $"Adaptador '{adapterName}' já está pronto.");
            return;
        }

        string? tool = FindTapTool();

        if (tool is null)
        {
            Report(progress, "Driver TAP não encontrado. Obtendo o instalador oficial da OpenVPN...");
            string installer = await AcquireInstallerAsync(progress, ct).ConfigureAwait(false);

            Report(progress, "Instalando o driver (silencioso). O Windows pode pedir confirmação do driver.");
            RunInstaller(installer);

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

            tool = FindTapTool()
                ?? throw new InvalidOperationException(
                    "O driver foi instalado, mas não encontrei tapctl.exe nem tapinstall.exe para criar o adaptador.");
        }

        adapters = TapAdapterLocator.FindAll();

        if (adapters.Count == 0)
        {
            Report(progress, "Criando o adaptador de rede virtual...");
            CreateAdapter(tool, adapterName, progress);

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            adapters = TapAdapterLocator.FindAll();
        }

        if (adapters.Count == 0)
            throw new InvalidOperationException(BuildFailureMessage());

        // tapctl já cria com o nome certo; este passo cobre o tapinstall (nome default) e
        // adaptadores pré-existentes com outro nome.
        if (!adapters.Any(a => string.Equals(a.Name, adapterName, StringComparison.OrdinalIgnoreCase)))
        {
            var target = adapters[0];
            Report(progress, $"Renomeando '{target.Name}' → '{adapterName}'...");

            if (!RenameAdapter(target.Name, adapterName))
                Log.Warn($"Não consegui renomear o adaptador para '{adapterName}'. Ele funciona pelo nome atual.");

            adapters = TapAdapterLocator.FindAll();
        }

        // Reporta o nome REAL do adaptador pronto — se o rename falhou, o app usa esse nome.
        string readyName =
            adapters.FirstOrDefault(a => string.Equals(a.Name, adapterName, StringComparison.OrdinalIgnoreCase))?.Name
            ?? adapters[0].Name;

        Report(progress, $"Adaptador '{readyName}' pronto para uso.");
    }

    // ---------------------------------------------------------------- Localização das ferramentas

    /// <summary>tapctl vem no OpenVPN moderno; tapinstall (devcon) vem no pacote tap-windows standalone.</summary>
    private static string? FindTapTool()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] candidates =
        [
            Path.Combine(programFiles, "OpenVPN", "bin", "tapctl.exe"),
            Path.Combine(programFiles, "TAP-Windows", "bin", "tapinstall.exe"),
            Path.Combine(programFilesX86, "TAP-Windows", "bin", "tapinstall.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------------------------------------------------------------- Obtenção do instalador

    /// <summary>
    /// Retorna o caminho de um instalador do tap-windows. Prefere um arquivo local (pacote
    /// offline); só baixa se não houver nenhum ao lado do app.
    /// </summary>
    private static async Task<string> AcquireInstallerAsync(IProgress<string>? progress, CancellationToken ct)
    {
        string? local = FindLocalInstaller();
        if (local is not null)
        {
            Report(progress, $"Usando instalador local: {Path.GetFileName(local)}");
            return local;
        }

        string destination = Path.Combine(Path.GetTempPath(), "tap-windows-installer.exe");

        Report(progress, "Baixando o driver do site oficial da OpenVPN (~1 MB)...");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            await using var source = await http.GetStreamAsync(InstallerUrl, ct).ConfigureAwait(false);
            await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Falha ao baixar o driver TAP de {InstallerUrl}: {ex.Message}\n\n" +
                "Alternativa: instale o OpenVPN (https://openvpn.net/community-downloads/) marcando o " +
                "componente 'TAP Virtual Ethernet Adapter', ou coloque um arquivo 'tap-windows*.exe' " +
                "na pasta do VirtualLan e tente de novo.", ex);
        }

        return destination;
    }

    /// <summary>Procura um instalador do TAP ao lado do executável (e numa subpasta 'drivers').</summary>
    private static string? FindLocalInstaller()
    {
        string baseDir = AppContext.BaseDirectory;

        string[] searchDirs = [baseDir, Path.Combine(baseDir, "drivers")];

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;

            string? match = Directory.EnumerateFiles(dir, "tap-windows*.exe").FirstOrDefault();
            if (match is not null) return match;
        }

        return null;
    }

    private static void RunInstaller(string installerPath)
    {
        // O instalador do tap-windows é NSIS: /S = silencioso.
        var psi = new ProcessStartInfo(installerPath, "/S") { UseShellExecute = false };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Não foi possível iniciar o instalador do driver TAP.");

        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"O instalador do driver TAP retornou o código {process.ExitCode}.");
    }

    // ---------------------------------------------------------------- Criação do adaptador

    private static void CreateAdapter(string tool, string adapterName, IProgress<string>? progress)
    {
        int exit;
        string output;

        if (tool.EndsWith("tapctl.exe", StringComparison.OrdinalIgnoreCase))
        {
            (exit, output) = Run(tool, $"create --name \"{adapterName}\"", workingDirectory: null);
        }
        else
        {
            // tapinstall (devcon): precisa do OemVista.inf. Ele fica em <raiz>\driver, e não em
            // <raiz>\bin junto do tapinstall — por isso passamos o caminho completo do .inf.
            string binDir = Path.GetDirectoryName(tool)!;
            string root = Path.GetDirectoryName(binDir)!;
            string driverDir = Path.Combine(root, "driver");
            string inf = Path.Combine(driverDir, "OemVista.inf");

            if (File.Exists(inf))
                (exit, output) = Run(tool, $"install \"{inf}\" tap0901", workingDirectory: driverDir);
            else
                (exit, output) = Run(tool, "install OemVista.inf tap0901", workingDirectory: binDir);
        }

        if (exit != 0)
        {
            // Ambas as ferramentas saem com código != 0 quando o adaptador já existe; para nós
            // o objetivo é "existir um adaptador", não "ter acabado de criar um".
            if (output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                Report(progress, "    (o adaptador já existia; seguindo)");
            else
                throw new InvalidOperationException($"Criação do adaptador falhou (código {exit}).\n{output}");
        }
    }

    private static bool RenameAdapter(string oldName, string newName)
    {
        var (exit, output) = Run("netsh", $"interface set interface name=\"{oldName}\" newname=\"{newName}\"", workingDirectory: null);
        if (exit != 0) Log.Debug($"netsh rename '{oldName}'→'{newName}' saiu com {exit}. {output}");
        return exit == 0;
    }

    // ---------------------------------------------------------------- Utilitários

    private static (int ExitCode, string Output) Run(string fileName, string arguments, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Não foi possível iniciar '{fileName}'.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, $"{stdout}\n{stderr}".Trim());
    }

    private static string BuildFailureMessage()
    {
        var all = TapAdapterLocator.EnumerateNetworkClass();

        var message = new StringBuilder();
        message.AppendLine("Nenhum adaptador TAP compatível após a instalação.");
        message.AppendLine();
        message.AppendLine($"Adaptadores de rede no registro ({all.Count}):");
        foreach (var e in all)
        {
            string mark = TapAdapterLocator.IsSupportedComponentId(e.ComponentId) ? "*" : " ";
            message.AppendLine($" {mark} [{e.SubKey}] {e.ComponentId,-24} {e.Name ?? "(sem nome)"}");
        }
        message.AppendLine();
        message.AppendLine("Se aparece um 'TAP-Windows Adapter V9' no Gerenciador de Dispositivos mas nada acima,");
        message.AppendLine("reinicie o Windows e tente de novo. * = compatível com o VirtualLan.");

        return message.ToString();
    }

    private static void Report(IProgress<string>? progress, string message)
    {
        Log.Info(message);
        progress?.Report(message);
    }
}
