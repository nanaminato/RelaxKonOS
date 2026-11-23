using RelaxKonOS.Protocol.Databases;

using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.ServerModule.Database;

public sealed class DatabaseQueries(IDatabaseQueryHost host, IPostgreSqlBackupLedger ledger) : IDatabaseQueries
{
    public async Task<PostgreSqlState> ReadPostgreSqlAsync(Guid? instance, CancellationToken cancellationToken)
    {
        var state = await host.ReadPostgreSqlAsync(instance, cancellationToken);
        return PostgreSqlObservation.Capture(state, ledger).ToPublicState();
    }
}
