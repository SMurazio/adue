namespace Mmo.Client.Web;

public sealed record BridgeOptions(
    string GameHost,
    int GamePort,
    string ConnectionKey,
    string Name);
