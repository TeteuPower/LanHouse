using System.Runtime.InteropServices;
using System.Security.Principal;
using VirtualLan.Core.Diagnostics;
using VirtualLan.Node;
using VirtualLan.Node.Tap;

const string Usage = """
    vlan — cliente do VirtualLan (rede LAN virtual para jogos)

      --relay,   -r <host:porta>   Servidor de relay. Obrigatório.
      --network, -n <nome>         Nome da rede virtual. Obrigatório.
      --password,-k <senha>        Senha da rede. Obrigatória.
      --adapter, -a <nome>         Nome do adaptador TAP (se houver mais de um).
      --port         <n>           Porta UDP local (padrão: efêmera).
      --list-adapters              Lista os adaptadores TAP instalados e sai.
      --verbose, -v                Log de depuração.
      --trace                      Log de trace.
      --help,    -h                Esta ajuda.

    Exemplo:
      vlan -r vpn.exemplo.com:7777 -n dota-sexta -k "senha forte aqui"

    Todos os participantes precisam usar EXATAMENTE o mesmo nome de rede e senha.
    Requer privilégio de administrador.
    """;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("O cliente VirtualLan só roda no Windows (driver tap-windows6).");
    return 1;
}

string? relay = null, network = null, password = null, adapter = null;
int localPort = 0;
var level = LogLevel.Info;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];

    switch (arg)
    {
        case "--help" or "-h":
            Console.WriteLine(Usage);
            return 0;

        case "--list-adapters":
            ListAdapters();
            return 0;

        case "--verbose" or "-v":
            level = LogLevel.Debug;
            continue;

        case "--trace":
            level = LogLevel.Trace;
            continue;
    }

    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine($"'{arg}' exige um valor.\n\n{Usage}");
        return 1;
    }

    string value = args[++i];

    switch (arg)
    {
        case "--relay" or "-r": relay = value; break;
        case "--network" or "-n": network = value; break;
        case "--password" or "-k": password = value; break;
        case "--adapter" or "-a": adapter = value; break;

        case "--port":
            if (!int.TryParse(value, out localPort) || localPort is < 0 or > 65535)
            {
                Console.Error.WriteLine("--port exige um número entre 0 e 65535.");
                return 1;
            }
            break;

        default:
            Console.Error.WriteLine($"Argumento desconhecido: {arg}\n\n{Usage}");
            return 1;
    }
}

if (relay is null || network is null || password is null)
{
    Console.Error.WriteLine($"--relay, --network e --password são obrigatórios.\n\n{Usage}");
    return 1;
}

if (!TrySplitHostPort(relay, defaultPort: 7777, out string relayHost, out int relayPort))
{
    Console.Error.WriteLine($"Endereço de relay inválido: '{relay}'. Use host:porta.");
    return 1;
}

if (!IsElevated())
{
    Console.Error.WriteLine("Execute como Administrador: abrir o adaptador TAP e configurar IP exigem elevação.");
    return 1;
}

Log.MinimumLevel = level;

var options = new NodeOptions
{
    RelayHost = relayHost,
    RelayPort = relayPort,
    NetworkName = network,
    Password = password,
    AdapterName = adapter,
    LocalPort = localPort,
};

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log.Info("Saindo da rede virtual...");
    cts.Cancel();
};

using var node = new NodeService(options);

try
{
    await node.RunAsync(cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Log.Error("Falha fatal", ex);
    return 1;
}

static void ListAdapters()
{
    if (!OperatingSystem.IsWindows()) return;

    var adapters = TapAdapterLocator.FindAll();

    if (adapters.Count > 0)
    {
        Console.WriteLine($"{adapters.Count} adaptador(es) TAP compatível(is):");
        foreach (var a in adapters) Console.WriteLine($"  {a}");
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine("Nenhum adaptador TAP compatível encontrado.");
        Console.WriteLine("Rode scripts\\install-tap.ps1 como administrador.");
        Console.WriteLine();
    }

    // Sempre despeja a classe de rede inteira: permite diagnosticar um ComponentId
    // inesperado sem abrir o regedit.
    var all = TapAdapterLocator.EnumerateNetworkClass();
    Console.WriteLine($"Todos os adaptadores de rede no registro ({all.Count}):");

    foreach (var e in all)
    {
        string mark = TapAdapterLocator.IsSupportedComponentId(e.ComponentId) ? "*" : " ";
        Console.WriteLine($" {mark} [{e.SubKey}] {e.ComponentId,-26} {e.Name ?? "(sem nome)"}");
    }

    Console.WriteLine();
    Console.WriteLine("  * = compativel com o VirtualLan");
}

static bool IsElevated()
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;

#pragma warning disable CA1416 // guardado acima
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
}

static bool TrySplitHostPort(string text, int defaultPort, out string host, out int port)
{
    host = text;
    port = defaultPort;

    int colon = text.LastIndexOf(':');
    if (colon < 0) return text.Length > 0;

    host = text[..colon];
    return host.Length > 0 && int.TryParse(text[(colon + 1)..], out port) && port is > 0 and <= 65535;
}
