using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.ServerModule.Database.Contracts;

public interface IDatabaseExecution
{
    void AddServices(IServiceCollection services);
    IDatabaseCredentialStore OpenCredentialStore(string root, IDataProtector protector);
}

public interface IDatabaseCredentialStore
{
    PostgreSqlCredentialReference Stage(PostgreSqlCredentialRequest request, string actorReference);
    string Read(Guid reference, Guid instance, string? actorReference = null);
    void DeleteInstance(Guid instance);
}

public sealed class DatabaseCredentialException(string problemCode, int statusCode = 409) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public int StatusCode { get; } = statusCode;
}
