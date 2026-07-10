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

        lock (Gate)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write($"{DateTime.Now:HH:mm:ss.fff} [{tag}] ");
            Console.ForegroundColor = previous;
            Console.WriteLine(message);
        }
    }
}
