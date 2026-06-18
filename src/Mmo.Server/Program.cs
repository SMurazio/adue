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

Log.Info($"Starting MMO server on UDP port {options.Port} at {options.TickRate} ticks/sec.");
Log.Info($"Database provider: {options.DatabaseProvider}; migrations: {options.MigrationsPath}.");

var (databaseInitializer, characterRepository) = options.DatabaseProvider switch
{
    DatabaseProvider.Sqlite => (
        (IDatabaseInitializer)new SqliteMigrationRunner(options.ConnectionString, options.MigrationsPath),
        (ICharacterRepository)new SqliteCharacterRepository(options.ConnectionString)),
    DatabaseProvider.Postgres => (
        new PostgresMigrationRunner(options.ConnectionString, options.MigrationsPath),
        new PostgresCharacterRepository(options.ConnectionString)),
    _ => throw new InvalidOperationException($"Unsupported database provider {options.DatabaseProvider}.")
};

await databaseInitializer.ApplyAsync(shutdown.Token);
var server = new GameServer(options, characterRepository);

await server.RunAsync(shutdown.Token);
