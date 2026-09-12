using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal static class SambaShareConfiguration
{
    public static string Serialize(IReadOnlyList<SmbManagedShareRequest> shares)
    {
        var builder = new System.Text.StringBuilder("# RelaxKonOS managed Samba include. Do not edit.\nserver min protocol = SMB2\nclient min protocol = SMB2\nguest account = nobody\nmap to guest = Never\n");
        if (shares.Any(x => x.GuestAllowed)) builder.Replace("map to guest = Never", "map to guest = Bad User");
        foreach (var share in shares.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            builder.Append("\n# relaxkonos-share:").Append(share.Id).Append('\n').Append('[').Append(share.Name).Append("]\npath = ").Append(share.Path).Append('\n')
                .Append("# relaxkonos-read-only:").Append(share.ReadOnly ? "yes" : "no").Append('\n')
                .Append("read only = ").Append(share.ReadOnly || share.GuestAllowed ? "yes" : "no").Append('\n').Append("available = ").Append(share.Enabled ? "yes" : "no").Append('\n')
                .Append("guest ok = ").Append(share.GuestAllowed ? "yes" : "no").Append('\n');
            if (!string.IsNullOrWhiteSpace(share.Description)) builder.Append("comment = ").Append(share.Description).Append('\n');
            var read = share.Permissions.Where(x => x.Access == "Read" || share.ReadOnly).Select(x => x.Principal).Order().ToArray();
            var write = share.Permissions.Where(x => x.Access == "ReadWrite" && !share.ReadOnly).Select(x => x.Principal).Order().ToArray();
            if (read.Length > 0) builder.Append("read list = ").AppendJoin(' ', read).Append('\n');
            builder.Append("write list = ").AppendJoin(' ', write).Append('\n');
        }
        return builder.ToString();
    }

    public static IReadOnlyList<FileShareDto> Parse(string[] lines, Func<SmbManagedShareRequest, bool> isValid)
    {
        var shares = new List<FileShareDto>(); string? id = null, name = null, path = null, description = null; bool readOnly = true, enabled = true, guest = false; bool? configuredReadOnly = null, requestedReadOnly = null; var permissions = new List<FileSharePermissionDto>();
        void Commit()
        {
            if (id is null && name is null) return;
            if (requestedReadOnly is null || configuredReadOnly != (requestedReadOnly.Value || guest)) throw new InvalidDataException();
            if (id is null || name is null || path is null || !isValid(new(id, name, path, description, readOnly, enabled, guest, permissions.Select(x => new SmbSharePermissionRequest(x.Principal, x.Access.ToString())).ToArray()))) throw new InvalidDataException();
            shares.Add(new(id, name, path, description, readOnly, enabled, guest, permissions.ToArray(), true)); id = name = path = description = null; readOnly = true; enabled = true; guest = false; configuredReadOnly = requestedReadOnly = null; permissions.Clear();
        }
        foreach (var raw in lines)
        {
            var line = raw.Trim(); if (line.Length == 0) continue; const string shareMarker = "# relaxkonos-share:"; if (line.StartsWith(shareMarker, StringComparison.Ordinal)) { Commit(); id = line[shareMarker.Length..]; continue; }
            if (id is null) continue;
            const string readOnlyMarker = "# relaxkonos-read-only:";
            if (line.StartsWith(readOnlyMarker, StringComparison.Ordinal)) { requestedReadOnly = readOnly = line[readOnlyMarker.Length..] switch { "yes" => true, "no" => false, _ => throw new InvalidDataException() }; continue; }
            if (line.StartsWith('[') && line.EndsWith(']')) { name = line[1..^1]; continue; }
            var index = line.IndexOf('='); if (index < 1) throw new InvalidDataException(); var key = line[..index].Trim(); var value = line[(index + 1)..].Trim();
            switch (key) { case "path": path = value; break; case "comment": description = value; break; case "read only": configuredReadOnly = value switch { "yes" => true, "no" => false, _ => throw new InvalidDataException() }; break; case "available": enabled = value == "yes"; break; case "guest ok": guest = value == "yes"; break;
                case "read list": permissions.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => new FileSharePermissionDto(x, FileShareAccess.Read))); break;
                case "write list": permissions.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => new FileSharePermissionDto(x, FileShareAccess.ReadWrite))); break;
                default: throw new InvalidDataException(); }
        }
        Commit(); return shares;
    }

}
