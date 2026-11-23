using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.ServerModule.Database.Contracts;

public interface IDatabaseExecution
{
    void AddBundledObservation(IServiceCollection services);
}
