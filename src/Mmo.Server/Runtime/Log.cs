namespace Mmo.Server.Runtime;

public static class Log
{
    public static void Info(string message)
    {
        Write("info", message);
    }

    public static void Warn(string message)
    {
        Write("warn", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("error", exception is null ? message : $"{message}: {exception.Message}");
    }

    private static void Write(string level, string message)
    {
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} [{level}] {message}");
    }
}
