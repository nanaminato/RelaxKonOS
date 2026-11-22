using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.Server.Databases;

/// <summary>Supplies the host key authority and binds stored references to the installation actor.</summary>
public sealed class PostgreSqlCredentialStore(IHostEnvironment environment, IDataProtectionProvider protection)
{
    private readonly IDatabaseCredentialStore _store = BundledDatabaseExecution.Instance.OpenCredentialStore(
        Path.Combine(environment.ContentRootPath, "data", "database-secrets"),
        protection.CreateProtector("RelaxKonOS.Database.Credentials.v1"));

    public PostgreSqlCredentialReference Stage(PostgreSqlCredentialRequest request, string actor)
    {
        try { return _store.Stage(request, InstallationOperationStore.Reference(actor)); }
        catch (DatabaseCredentialException error) { throw Translate(error); }
    }

    public string Read(Guid reference, Guid instance, string? actor = null)
    {
        try { return _store.Read(reference, instance, actor is null ? null : InstallationOperationStore.Reference(actor)); }
        catch (DatabaseCredentialException error) { throw Translate(error); }
    }

    public void DeleteInstance(Guid instance)
    {
        try { _store.DeleteInstance(instance); }
        catch (DatabaseCredentialException error) { throw Translate(error); }
    }

    private static InstallationException Translate(DatabaseCredentialException error) =>
        new(error.ProblemCode, error.StatusCode);
}
