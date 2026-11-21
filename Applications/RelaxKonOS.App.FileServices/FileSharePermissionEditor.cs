using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.FileServices;

namespace RelaxKonOS.App.FileServices;

public sealed partial class FileSharePermissionEditor : ObservableObject
{
    public bool IsWindowsServer { get; }
    public IReadOnlyList<FileSharePrincipalOption> PrincipalOptions { get; }
    public bool HasPrincipalOptions => PrincipalOptions.Count > 0;
    [ObservableProperty] private FileSharePrincipalOption? _selectedPrincipal;
    partial void OnSelectedPrincipalChanged(FileSharePrincipalOption? value) { if (value is not null) Principal = value.Value; }
    private readonly IReadOnlyList<FileShareAccessOption> _accessOptions;
    public IReadOnlyList<FileShareAccessOption> AccessOptions => _accessOptions;
    [ObservableProperty] private string _principal;
    [ObservableProperty] private FileShareAccessOption _selectedAccess;

    public FileSharePermissionEditor(Func<string, string> localize, string principal = "", FileShareAccess access = FileShareAccess.Read, bool isWindowsServer = false, IEnumerable<string>? linuxUsers = null)
    {
        IsWindowsServer = isWindowsServer;
        _accessOptions = FileShareAccessOption.Create(localize);
        PrincipalOptions = isWindowsServer
            ? [new("S-1-5-32-544", localize("file_services.principal.administrators")), new("S-1-5-32-545", localize("file_services.principal.users")),
               new("S-1-5-11", localize("file_services.principal.authenticated_users")), new("S-1-1-0", localize("file_services.principal.everyone"))]
            : (linuxUsers ?? []).Distinct(StringComparer.Ordinal).OrderBy(username => username, StringComparer.Ordinal).Select(username => new FileSharePrincipalOption(username, username)).ToArray();
        _principal = principal;
        _selectedPrincipal = PrincipalOptions.FirstOrDefault(option => string.Equals(option.Value, principal, StringComparison.OrdinalIgnoreCase));
        _selectedAccess = _accessOptions.First(x => x.Value == access);
    }
}

public sealed record FileShareAccessOption(FileShareAccess Value, string Label)
{
    public static IReadOnlyList<FileShareAccessOption> Create(Func<string, string> localize) =>
    [
        new(FileShareAccess.Read, localize("file_services.access.Read")),
        new(FileShareAccess.ReadWrite, localize("file_services.access.ReadWrite"))
    ];
}

public sealed record FileSharePrincipalOption(string Value, string Label);
