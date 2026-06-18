using System.Globalization;

namespace Mmo.Tools.Stress;

public sealed record StressOptions(
    string Host,
    int Port,
    string ConnectionKey,
    int Clients,
    TimeSpan Duration,
    double SpawnRatePerSecond,
    TimeSpan MoveInterval,
    TimeSpan DirectionInterval,
    TimeSpan ReportInterval,
    TimeSpan ChatInterval,
    double MinAuthRate,
    int MaxErrors,
    string NamePrefix,
    int Seed,
    bool ShowHelp)
{
    public static StressOptions FromArgs(string[] args)
    {
        var options = new StressOptions(
            ReadString(args, "--host=", "127.0.0.1"),
            ReadInt(args, "--port=", 7777),
            ReadString(args, "--key=", "local-dev"),
            ReadInt(args, "--clients=", 50),
            ReadDuration(args, "--duration=", TimeSpan.FromSeconds(60)),
            ReadDouble(args, "--spawn-rate=", 25),
            ReadDuration(args, "--move-interval=", TimeSpan.FromMilliseconds(250)),
            ReadDuration(args, "--direction-interval=", TimeSpan.FromSeconds(1)),
            ReadDuration(args, "--report-interval=", TimeSpan.FromSeconds(5)),
            ReadDuration(args, "--chat-interval=", TimeSpan.Zero),
            ReadDouble(args, "--min-auth-rate=", 1),
            ReadInt(args, "--max-errors=", 0),
            ReadString(args, "--name-prefix=", $"Load{DateTimeOffset.UtcNow:HHmmss}"),
            ReadInt(args, "--seed=", Environment.TickCount),
            args.Any(arg => arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("-h", StringComparison.OrdinalIgnoreCase)));

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (ShowHelp)
        {
            return;
        }

        if (Clients < 1)
        {
            throw new ArgumentException("--clients must be at least 1.");
        }

        if (Port < 1 || Port > 65535)
        {
            throw new ArgumentException("--port must be between 1 and 65535.");
        }

        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("--duration must be greater than zero.");
        }

        if (SpawnRatePerSecond <= 0)
        {
            throw new ArgumentException("--spawn-rate must be greater than zero.");
        }

        if (MoveInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("--move-interval must be greater than zero.");
        }

        if (DirectionInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("--direction-interval must be greater than zero.");
        }

        if (ReportInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("--report-interval must be greater than zero.");
        }

        if (ChatInterval < TimeSpan.Zero)
        {
            throw new ArgumentException("--chat-interval cannot be negative.");
        }

        if (MinAuthRate < 0 || MinAuthRate > 1)
        {
            throw new ArgumentException("--min-auth-rate must be between 0 and 1.");
        }

        if (MaxErrors < 0)
        {
            throw new ArgumentException("--max-errors cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(NamePrefix))
        {
            throw new ArgumentException("--name-prefix cannot be empty.");
        }
    }

    private static string ReadString(string[] args, string prefix, string fallback)
    {
        var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match is null ? fallback : match[prefix.Length..];
    }

    private static int ReadInt(string[] args, string prefix, int fallback)
    {
        var value = ReadString(args, prefix, "");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double ReadDouble(string[] args, string prefix, double fallback)
    {
        var value = ReadString(args, prefix, "");
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static TimeSpan ReadDuration(string[] args, string prefix, TimeSpan fallback)
    {
        var value = ReadString(args, prefix, "");
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        value = value.Trim();
        var lower = value.ToLowerInvariant();
        if (lower.EndsWith("ms", StringComparison.Ordinal)
            && double.TryParse(lower[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        if (lower.EndsWith("s", StringComparison.Ordinal)
            && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (lower.EndsWith("m", StringComparison.Ordinal)
            && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bareSeconds))
        {
            return TimeSpan.FromSeconds(bareSeconds);
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}
