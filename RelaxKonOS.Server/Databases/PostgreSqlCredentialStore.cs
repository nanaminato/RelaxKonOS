using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Server.Installations;

namespace RelaxKonOS.Server.Databases;

/// <summary>Supplies the host key authority and binds stored references to the installation actor.</summary>
public sealed class PostgreSqlCredentialStore(IHostEnvironment environment, IDataProtectionProvider protection)
{
    private readonly DatabaseCredentialStore _store = new(
        Path.Combine(environment.ContentRootPath, "data", "database-secrets"),
        protection.CreateProtector("RelaxKonOS.Database.Credentials.v1"));

    public PostgreSqlCredentialReference Stage(PostgreSqlCredentialRequest request, string actor) =>
        _store.Stage(request, InstallationOperationStore.Reference(actor));

    public string Read(Guid reference, Guid instance, string? actor = null) =>
        _store.Read(reference, instance, actor is null ? null : InstallationOperationStore.Reference(actor));

    public void DeleteInstance(Guid instance) => _store.DeleteInstance(instance);
}
