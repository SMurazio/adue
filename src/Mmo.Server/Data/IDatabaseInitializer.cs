namespace Mmo.Server.Data;

public interface IDatabaseInitializer
{
    Task ApplyAsync(CancellationToken cancellationToken);
}
