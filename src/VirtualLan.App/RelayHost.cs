using System.Diagnostics;
using System.Runtime.Versioning;
using VirtualLan.Core.Diagnostics;
using VirtualLan.Relay;

namespace VirtualLan.App;

/// <summary>
/// Hospeda o relay dentro do próprio processo da GUI (modo "ser o servidor"), cuidando de tudo
/// que o usuário teria que fazer à mão: sobe o <see cref="RelayServer"/>, abre a porta no Firewall
/// do Windows, tenta abrir a porta no roteador via UPnP e descobre o IP público para compartilhar.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RelayHost : IDisposable
{
    private const string FirewallRuleName = "VirtualLan Relay";

    private readonly int _port;

    private RelayServer? _server;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private bool _firewallAdded;
    private bool _upnpMapped;

    public RelayHost(int port) => _port = port;

    /// <summary>IP público para o amigo usar como endereço do relay. Null se não foi possível descobrir.</summary>
    public string? PublicIp { get; private set; }

    /// <summary>Endereço completo (IP:porta) para compartilhar, ou null.</summary>
    public string? ShareAddress => PublicIp is null ? null : $"{PublicIp}:{_port}";

    public bool UpnpMapped => _upnpMapped;

    public async Task StartAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Iniciando o servidor (relay) na porta UDP {_port}...");

        _cts = new CancellationTokenSource();
        _server = new RelayServer(_port);
        _runTask = Task.Run(() => RunServerAsync(_server, _cts.Token), CancellationToken.None);

        _firewallAdded = AddFirewallRule(_port);
        if (_firewallAdded) progress?.Report("Porta liberada no Firewall do Windows.");

        progress?.Report("Tentando abrir a porta no roteador automaticamente (UPnP)...");
        var map = await Upnp.TryMapUdpAsync(_port, "VirtualLan relay", ct).ConfigureAwait(false);
        _upnpMapped = map.Success;

        PublicIp = map.ExternalIp ?? await TryGetPublicIpAsync(ct).ConfigureAwait(false);

        if (map.Success)
            progress?.Report("Porta aberta automaticamente no roteador (UPnP). Tudo pronto para o amigo conectar.");
        else
            progress?.Report(
                $"UPnP indisponível ({map.Detail}). Se o amigo não conectar, encaminhe a porta UDP {_port} " +
                "para este PC no seu roteador (uma vez só).");

        if (ShareAddress is not null)
            progress?.Report($"Endereço para o amigo usar como Servidor: {ShareAddress}");
    }

    private static async Task RunServerAsync(RelayServer server, CancellationToken ct)
    {
        try
        {
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* parada normal */ }
        catch (Exception ex)
        {
            Log.Error("O servidor (relay) parou com erro", ex);
        }
    }

    private static async Task<string?> TryGetPublicIpAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string ip = (await http.GetStringAsync("https://api.ipify.org", ct).ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(ip) ? null : ip;
        }
        catch
        {
            return null;
        }
    }

    private static bool AddFirewallRule(int port)
    {
        RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
        int exit = RunNetsh(
            $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=UDP localport={port}");
        return exit == 0;
    }

    private static void RemoveFirewallRule()
        => RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");

    private static int RunNetsh(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(psi);
            if (process is null) return -1;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* já parado */ }

        try
        {
            if (_upnpMapped)
                Upnp.TryUnmapUdpAsync(_port, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch { /* melhor-esforço */ }

        try { if (_firewallAdded) RemoveFirewallRule(); } catch { /* melhor-esforço */ }

        try { _server?.Dispose(); } catch { /* já disposto */ }
        try { _cts?.Dispose(); } catch { /* já disposto */ }
    }
}
