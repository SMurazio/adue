using Mmo.Server.Configuration;
using Mmo.Server.Data;
using Mmo.Server.Runtime;

var options = ServerOptions.FromEnvironment();
using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    Log.Info($"Starting MMO server on UDP port {options.Port} at {options.TickRate} ticks/sec.");
    Log.Info($"Database provider: {options.DatabaseProvider}; migrations: {options.MigrationsPath}.");
    Log.Info($"Demo mode (auto-pair + solo-start guard): {(options.DemoMode ? "ON" : "off")}.");

    // ECOLOGY E3 (docs/ecology-v1-design.md D8): region_populations only has a Sqlite migration/repository in
    // this task's scope (Postgres has zero test coverage today and is not the active provider — see the review
    // briefing) — the Postgres branch wires NullEcologyRepository so ecology simply stays unpersisted there
    // rather than crashing boot.
    var (databaseInitializer, characterRepository, ecologyRepository) = options.DatabaseProvider switch
    {
        DatabaseProvider.Sqlite => (
            (IDatabaseInitializer)new SqliteMigrationRunner(options.ConnectionString, options.MigrationsPath),
            (ICharacterRepository)new SqliteCharacterRepository(options.ConnectionString),
            (IEcologyRepository)new SqliteEcologyRepository(options.ConnectionString)),
        DatabaseProvider.Postgres => (
            new PostgresMigrationRunner(options.ConnectionString, options.MigrationsPath),
            new PostgresCharacterRepository(options.ConnectionString),
            new NullEcologyRepository()),
        _ => throw new InvalidOperationException($"Unsupported database provider {options.DatabaseProvider}.")
    };

    // E3 review L4: silence here would be the exact D8 lie ("a restart that heals the world") discoverable only
    // by reading source — say it out loud at boot instead.
    if (ecologyRepository is NullEcologyRepository)
    {
        Log.Warn("Ecology persistence is DISABLED for this database provider — region populations reset to their K-seeds on every restart.");
    }

    await databaseInitializer.ApplyAsync(shutdown.Token);
    var server = new GameServer(options, characterRepository, ecologyRepository);

    await server.RunAsync(shutdown.Token);
}
finally
{
    Log.Flush();
}
