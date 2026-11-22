using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.Settings.Views;

public abstract partial class AppPermissionEditor(
    string appId,
    Func<string> appName,
    Func<string, string> labels,
    IReadOnlyList<AppPermissionCategoryViewModel> groups,
    Action<string, bool> setGrant,
    Action<bool> complete) : ObservableObject
{
    public string AppId { get; } = appId;
    public string AppName => appName();
    public Func<string, string> Labels { get; } = labels;
    public IReadOnlyList<AppPermissionCategoryViewModel> PermissionGroups { get; } = groups;

    [RelayCommand]
    private void Save()
    {
        foreach (var permission in PermissionGroups.SelectMany(group => group.Permissions))
            if (permission.HasChanged)
                setGrant(permission.PermissionId, permission.IsGranted);
        complete(true);
    }

    [RelayCommand]
    private void Cancel() => complete(false);

    protected void RefreshLanguage()
    {
        OnPropertyChanged(nameof(AppName));
        OnPropertyChanged(nameof(Labels));
        foreach (var group in PermissionGroups)
            group.RefreshLocalizedText();
    }
}

public sealed partial class AppPermissionCategoryViewModel : ObservableObject
{
    private readonly Func<string> _category;

    public AppPermissionCategoryViewModel(Func<string> category, IReadOnlyList<AppPermissionChoiceViewModel> permissions)
    {
        _category = category;
        Permissions = permissions;
        RefreshLocalizedText();
    }

    [ObservableProperty] private string _name = string.Empty;
    public IReadOnlyList<AppPermissionChoiceViewModel> Permissions { get; }

    public void RefreshLocalizedText()
    {
        Name = _category();
        foreach (var permission in Permissions)
            permission.RefreshLocalizedText();
    }
}

public sealed partial class AppPermissionChoiceViewModel : ObservableObject
{
    private readonly Func<string> _name;
    private readonly Func<string> _getDescription;

    public AppPermissionChoiceViewModel(string id, bool isGranted, Func<string> name, Func<string> description)
    {
        _name = name;
        _getDescription = description;
        PermissionId = id;
        InitialGrant = isGranted;
        _isGranted = isGranted;
        RefreshLocalizedText();
    }

    public string PermissionId { get; }
    public bool InitialGrant { get; }
    public bool HasChanged => IsGranted != InitialGrant;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isGranted;

    public void RefreshLocalizedText()
    {
        DisplayName = _name();
        Description = _getDescription();
    }
}
