using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.Databases;

[JsonConverter(typeof(JsonStringEnumConverter<MySqlAction>))]
public enum MySqlAction { Install, Initialize, Start, Stop, Verify }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MySqlRequest(bool Confirmed, MySqlAction? Action = null, long Revision = 0, Guid? RecoverOperationId = null)
{
    public bool Valid => Confirmed && (RecoverOperationId is { } id
        ? id != Guid.Empty && Action is null && Revision == 0
        : Action is { } action && Enum.IsDefined(action) && Revision > 0);
}

public sealed record MySqlState(bool Supported, long Revision, bool Installed, Guid? InstanceId,
    bool Running, bool Verified, Guid? PendingOperationId, Guid? CommittedOperationId, string? ProblemCode);

public static class MySqlRoutes
{
    public const string State = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/databases/mysql";
}
