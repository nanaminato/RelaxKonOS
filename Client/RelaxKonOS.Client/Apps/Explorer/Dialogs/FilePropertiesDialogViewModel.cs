using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Dialogs;

/// <summary>Presentation and Linux POSIX-permission editing for the file properties dialog.</summary>
public sealed partial class FilePropertiesDialogViewModel : ObservableObject
{
    private readonly Action _close;
    private readonly Func<int, Task<FilePropertiesDto>>? _saveUnixPermissions;
    private bool _initializingPermissions;
    private bool _synchronizingPermissionOctalInput;
    private int _specialPermissionBits;

    public FilePropertiesDialogViewModel(
        FilePropertiesDto properties,
        Func<int, Task<FilePropertiesDto>>? saveUnixPermissions,
        Action close)
    {
        _close = close;
        _saveUnixPermissions = saveUnixPermissions;
        Properties = properties;
    }

    [ObservableProperty] private FilePropertiesDto _properties = null!;
    [ObservableProperty] private bool _ownerRead;
    [ObservableProperty] private bool _ownerWrite;
    [ObservableProperty] private bool _ownerExecute;
    [ObservableProperty] private bool _groupRead;
    [ObservableProperty] private bool _groupWrite;
    [ObservableProperty] private bool _groupExecute;
    [ObservableProperty] private bool _othersRead;
    [ObservableProperty] private bool _othersWrite;
    [ObservableProperty] private bool _othersExecute;
    [ObservableProperty] private bool _isSavingPermissions;
    [ObservableProperty] private string _permissionOctalInput = string.Empty;
    [ObservableProperty] private string? _permissionOctalError;
    [ObservableProperty] private string _permissionStatus = LocalizedText.Get("explorer.permissions.select_then_save");

    public string SizeText => Properties.Size is { } size ? LocalizedText.Format("explorer.properties.size_bytes", size) : "—";
    public bool CanEditPermissions => Properties.UnixMode is not null && _saveUnixPermissions is not null;
    public bool HasValidPermissionOctal => PermissionOctalError is null;
    public bool HasPermissionOctalError => PermissionOctalError is not null;

    partial void OnPropertiesChanged(FilePropertiesDto value)
    {
        OnPropertyChanged(nameof(SizeText));
        InitializePermissions();
    }

    partial void OnIsSavingPermissionsChanged(bool value) => SavePermissionsCommand.NotifyCanExecuteChanged();

    partial void OnPermissionOctalInputChanged(string value)
    {
        if (_synchronizingPermissionOctalInput)
            return;

        if (!TryParseUnixMode(value, out var mode))
        {
            PermissionOctalError = LocalizedText.Get("explorer.permissions.invalid_octal");
            SavePermissionsCommand.NotifyCanExecuteChanged();
            return;
        }

        _initializingPermissions = true;
        ApplyUnixMode(mode);
        _initializingPermissions = false;
        PermissionOctalError = null;
        PermissionStatus = LocalizedText.Get("explorer.permissions.unsaved_changes");
        SavePermissionsCommand.NotifyCanExecuteChanged();
    }

    partial void OnPermissionOctalErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasValidPermissionOctal));
        OnPropertyChanged(nameof(HasPermissionOctalError));
        SavePermissionsCommand.NotifyCanExecuteChanged();
    }

    partial void OnOwnerReadChanged(bool value) => OnPermissionsChanged();
    partial void OnOwnerWriteChanged(bool value) => OnPermissionsChanged();
    partial void OnOwnerExecuteChanged(bool value) => OnPermissionsChanged();
    partial void OnGroupReadChanged(bool value) => OnPermissionsChanged();
    partial void OnGroupWriteChanged(bool value) => OnPermissionsChanged();
    partial void OnGroupExecuteChanged(bool value) => OnPermissionsChanged();
    partial void OnOthersReadChanged(bool value) => OnPermissionsChanged();
    partial void OnOthersWriteChanged(bool value) => OnPermissionsChanged();
    partial void OnOthersExecuteChanged(bool value) => OnPermissionsChanged();

    [RelayCommand(CanExecute = nameof(CanSavePermissions))]
    private async Task SavePermissionsAsync()
    {
        if (_saveUnixPermissions is null)
            return;

        IsSavingPermissions = true;
        PermissionStatus = LocalizedText.Get("explorer.permissions.saving");
        try
        {
            Properties = await _saveUnixPermissions(CurrentUnixMode);
            PermissionStatus = LocalizedText.Format("explorer.permissions.saved", PermissionOctalInput);
        }
        catch (Exception ex)
        {
            PermissionStatus = LocalizedText.Format("explorer.permissions.save_failed", ex.Message);
        }
        finally
        {
            IsSavingPermissions = false;
        }
    }

    [RelayCommand]
    private void Close() => _close();

    private bool CanSavePermissions() => CanEditPermissions && HasValidPermissionOctal && !IsSavingPermissions;

    private void InitializePermissions()
    {
        _initializingPermissions = true;
        ApplyUnixMode(Properties.UnixMode ?? 0);
        _initializingPermissions = false;
        PermissionOctalError = null;
        SynchronizePermissionOctalInput();
        OnPropertyChanged(nameof(CanEditPermissions));
        SavePermissionsCommand.NotifyCanExecuteChanged();
    }

    private void OnPermissionsChanged()
    {
        if (_initializingPermissions)
            return;

        SynchronizePermissionOctalInput();
        PermissionStatus = LocalizedText.Get("explorer.permissions.unsaved_changes");
    }

    private void ApplyUnixMode(int mode)
    {
        _specialPermissionBits = mode & ~0x1FF;
        OwnerRead = HasMode(mode, 0x100);
        OwnerWrite = HasMode(mode, 0x80);
        OwnerExecute = HasMode(mode, 0x40);
        GroupRead = HasMode(mode, 0x20);
        GroupWrite = HasMode(mode, 0x10);
        GroupExecute = HasMode(mode, 0x8);
        OthersRead = HasMode(mode, 0x4);
        OthersWrite = HasMode(mode, 0x2);
        OthersExecute = HasMode(mode, 0x1);
    }

    private void SynchronizePermissionOctalInput()
    {
        _synchronizingPermissionOctalInput = true;
        PermissionOctalInput = FormatUnixMode(CurrentUnixMode);
        _synchronizingPermissionOctalInput = false;
    }

    private static bool TryParseUnixMode(string value, out int mode)
    {
        mode = 0;
        if (value.Length is < 3 or > 4 || value.Any(c => c is < '0' or > '7'))
            return false;

        mode = Convert.ToInt32(value, 8);
        return true;
    }

    private static string FormatUnixMode(int mode) => Convert.ToString(mode, 8).PadLeft(4, '0');

    private int CurrentUnixMode => _specialPermissionBits |
        (OwnerRead ? 0x100 : 0) | (OwnerWrite ? 0x80 : 0) | (OwnerExecute ? 0x40 : 0) |
        (GroupRead ? 0x20 : 0) | (GroupWrite ? 0x10 : 0) | (GroupExecute ? 0x8 : 0) |
        (OthersRead ? 0x4 : 0) | (OthersWrite ? 0x2 : 0) | (OthersExecute ? 0x1 : 0);

    private static bool HasMode(int mode, int flag) => (mode & flag) != 0;
}
