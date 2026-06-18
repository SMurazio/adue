namespace Mmo.Client.ConsoleApp;

public sealed record ClientOptions(string Host, int Port, string ConnectionKey, string Name, bool ShowSnapshots)
{
    public static ClientOptions FromArgs(string[] args)
    {
        return new ClientOptions(
            ReadString(args, "--host=", "127.0.0.1"),
            ReadInt(args, "--port=", 7777),
            ReadString(args, "--key=", "local-dev"),
            ReadString(args, "--name=", $"Player{Random.Shared.Next(1000, 9999)}"),
            ReadBool(args, "--snapshots"));
    }

    private static string ReadString(string[] args, string prefix, string fallback)
    {
        var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match is null ? fallback : match[prefix.Length..];
    }

    private static int ReadInt(string[] args, string prefix, int fallback)
    {
        var value = ReadString(args, prefix, "");
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool ReadBool(string[] args, string flag)
    {
        return args.Any(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase));
    }
}
