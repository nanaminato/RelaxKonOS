using RelaxKonOS.Client.Apps.Explorer.Converters;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>A presentation only: cloud IDs never become local filesystem paths.</summary>
internal sealed record ComputerDriveCard(string Name, string Detail, string Capacity, double? UsedPercent,
    string? Path, Guid? ConnectionId)
{
    public static ComputerDriveCard Local(FileSystemEntryDto entry)
    {
        var known = entry.Size is > 0 && entry.AvailableFreeSpace is >= 0 && entry.AvailableFreeSpace <= entry.Size;
        return new(entry.Name, entry.Path,
            (entry.Size is >= 0 || entry.AvailableFreeSpace is >= 0) ? EntrySizeToStringConverter.FormatDriveCapacity(entry) : LocalizedText.Get("explorer.capacity_unknown"),
            known ? 100d * (entry.Size!.Value - entry.AvailableFreeSpace!.Value) / entry.Size.Value : null,
            entry.Path, null);
    }

    public static ComputerDriveCard Cloud(CloudConnectionDto account) => new(account.Name,
        $"{account.Protocol} · {account.Id.ToString()[..8]}", LocalizedText.Get("explorer.capacity_unknown"), null, null, account.Id);
}
