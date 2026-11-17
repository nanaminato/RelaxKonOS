using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Databases;

[JsonConverter(typeof(JsonStringEnumConverter<MySqlMaintenanceAction>))]
public enum MySqlMaintenanceAction { Backup, Restore, Update, Repair, UninstallKeepData, UninstallDeleteData, Reinstall }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MySqlMaintenanceCommand(Guid InstanceId, MySqlMaintenanceAction Action,
    string Version, Guid? BackupId = null, string? DeleteConfirmation = null)
{
    [JsonIgnore]
    public bool Valid => InstanceId != Guid.Empty && Enum.IsDefined(Action) && Version is { Length: >= 5 and <= 20 }
        && Version.All(c => char.IsAsciiDigit(c) || c == '.')
        && (Action == MySqlMaintenanceAction.Restore ? BackupId is { } b && b != Guid.Empty : BackupId is null)
        && (Action == MySqlMaintenanceAction.UninstallDeleteData ? DeleteConfirmation == InstanceId.ToString("N") : DeleteConfirmation is null);
}

public sealed record MySqlBackup(Guid Id, string Version, DateTimeOffset CreatedAt);
