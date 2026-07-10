namespace VirtualLan.Core.Diagnostics;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// Logger mínimo para console. Deliberadamente sem dependência de Microsoft.Extensions.Logging:
/// o Node precisa ser um único .exe pequeno, sem árvore de pacotes.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>
    /// Escrito também no console. A GUI assina este evento para espelhar o log num painel.
    /// Só dispara para mensagens que passam pelo <see cref="MinimumLevel"/> — mesma regra do console.
    /// O handler roda fora do lock do console; um sink que faça UI deve marshalar para a sua thread.
    /// </summary>
    public static event Action<LogLevel, string>? MessageLogged;

    /// <summary>Quando false, não escreve no console (a GUI não tem console; evita exceções de I/O).</summary>
    public static bool WriteToConsole { get; set; } = true;

    public static void Trace(string message) => Write(LogLevel.Trace, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        if (level < MinimumLevel) return;

        var (tag, color) = level switch
        {
            LogLevel.Trace => ("TRC", ConsoleColor.DarkGray),
            LogLevel.Debug => ("DBG", ConsoleColor.Gray),
            LogLevel.Info => ("INF", ConsoleColor.Cyan),
            LogLevel.Warn => ("WRN", ConsoleColor.Yellow),
            _ => ("ERR", ConsoleColor.Red),
        };

        if (WriteToConsole)
        {
            lock (Gate)
            {
                try
                {
                    var previous = Console.ForegroundColor;
                    Console.ForegroundColor = color;
                    Console.Write($"{DateTime.Now:HH:mm:ss.fff} [{tag}] ");
                    Console.ForegroundColor = previous;
                    Console.WriteLine(message);
                }
                catch (IOException) { /* sem console anexado (processo GUI) */ }
            }
        }

        MessageLogged?.Invoke(level, message);
    }
}
