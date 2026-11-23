using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.ServerModule.Database;

public sealed class DatabaseExecution : IDatabaseExecution
{
    public void AddBundledObservation(IServiceCollection services) =>
        services.AddSingleton<IDatabaseQueries, DatabaseQueries>();
}
