using VirtualLan.Core.Diagnostics;
using VirtualLan.Relay;

const string Usage = """
    vlan-relay — servidor de rendezvous e relay do VirtualLan

      --port, -p <n>   Porta UDP (padrão: 7777)
      --verbose, -v    Log de depuração
      --trace          Log de trace
      --help, -h       Esta ajuda

    O relay não decifra tráfego: conhece apenas o networkId (derivado da senha por HKDF)
    e os endpoints públicos dos nós.
    """;

int port = 7777;
var level = LogLevel.Info;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];

    if (arg is "--help" or "-h")
    {
        Console.WriteLine(Usage);
        return 0;
    }

    if (arg is "--verbose" or "-v") { level = LogLevel.Debug; continue; }
    if (arg is "--trace") { level = LogLevel.Trace; continue; }

    if (arg is "--port" or "-p")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("--port exige um número entre 1 e 65535.");
            return 1;
        }

        i++;
        continue;
    }

    Console.Error.WriteLine($"Argumento desconhecido: {arg}\n\n{Usage}");
    return 1;
}

Log.MinimumLevel = level;

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log.Info("Encerrando...");
    cts.Cancel();
};

using var server = new RelayServer(port);

try
{
    await server.RunAsync(cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception ex)
{
    Log.Error("Falha fatal no relay", ex);
    return 1;
}
