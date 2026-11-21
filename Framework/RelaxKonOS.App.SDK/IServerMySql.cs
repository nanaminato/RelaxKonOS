using System.Net;
using RelaxKonOS.Protocol.Databases;

namespace RelaxKonOS.AppSDK;

public interface IServerMySql
{
    Task<MySqlState?> ReadAsync(CancellationToken cancellationToken);
    Task<HttpStatusCode> StageCredentialAsync(PostgreSqlCredentialRequest request, CancellationToken cancellationToken);
}
