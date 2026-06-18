namespace Mmo.Client.Core;

public sealed record ClientConnectionOptions(
    string Host,
    int Port,
    string ConnectionKey,
    string AccountName,
    string DisplayName,
    string ClientName = "mmo-client-core");
