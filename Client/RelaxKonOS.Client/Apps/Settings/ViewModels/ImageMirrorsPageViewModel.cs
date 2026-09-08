using System.Collections.ObjectModel;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.ImageMirrors;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Manages per-user Docker Hub-compatible registry prefixes persisted by the server.</summary>
public sealed partial class ImageMirrorsPageViewModel : SettingsPageViewModel
{
    private readonly IImageMirrorClient _client;
    private readonly IAuthSession _session;

    public ImageMirrorsPageViewModel(ShellSettings settings, IImageMirrorClient client, IAuthSession session)
        : base(settings, save: null)
    {
        _client = client;
        _session = session;
    }

    public override string Glyph => "🪞";
    public override string DisplayNameKey => "settings.page.image_mirrors";
    public override string DisplayName => "Image mirrors";
    public ObservableCollection<ImageMirrorItemViewModel> Mirrors { get; } = [];
    public bool IsConnected => _session.State == AuthSessionState.Authenticated;

    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newEndpoint = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = string.Empty;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public async Task LoadAsync()
    {
        if (!IsConnected) return;
        IsLoading = true;
        StatusText = string.Empty;
        try
        {
            var mirrors = await _client.ListAsync(ImageMirrorTarget.Docker);
            Mirrors.Clear();
            foreach (var mirror in mirrors)
                Mirrors.Add(new ImageMirrorItemViewModel(mirror));
        }
        catch (Exception ex)
        {
            StatusText = string.Format(T("settings.image_mirrors.load_failed", "Could not load image mirrors: {0}"), ex.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewEndpoint))
        {
            StatusText = T("settings.image_mirrors.required", "Enter a name and registry host.");
            return;
        }
        IsLoading = true;
        try
        {
            await _client.CreateAsync(ImageMirrorTarget.Docker, new CreateImageMirrorRequest(NewName.Trim(), NewEndpoint.Trim()));
            NewName = NewEndpoint = string.Empty;
            StatusText = T("settings.image_mirrors.added", "Image mirror added.");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(T("settings.image_mirrors.save_failed", "Could not save image mirror: {0}"), ex.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SelectAsync(ImageMirrorItemViewModel? mirror)
    {
        if (mirror is null || IsLoading || mirror.IsSelected) return;
        IsLoading = true;
        try
        {
            await _client.SelectAsync(ImageMirrorTarget.Docker, mirror.IsDefault ? null : mirror.Id);
            SetSelected(mirror.Id);
            StatusText = mirror.IsDefault
                ? T("settings.image_mirrors.default_selected", "Docker will pull directly from its default registry.")
                : string.Format(T("settings.image_mirrors.selected", "Using {0} for Docker Hub pulls."), mirror.Name);
        }
        catch (Exception ex)
        {
            RefreshSelection();
            StatusText = string.Format(T("settings.image_mirrors.select_failed", "Could not select image mirror: {0}"), ex.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task RemoveAsync(ImageMirrorItemViewModel? mirror)
    {
        if (mirror is null || mirror.IsDefault || IsLoading) return;
        IsLoading = true;
        try
        {
            await _client.DeleteAsync(ImageMirrorTarget.Docker, mirror.Id);
            Mirrors.Remove(mirror);
            if (mirror.IsSelected) SetSelected(Guid.Empty);
            StatusText = T("settings.image_mirrors.removed", "Image mirror removed.");
        }
        catch (Exception ex)
        {
            StatusText = string.Format(T("settings.image_mirrors.remove_failed", "Could not remove image mirror: {0}"), ex.Message);
        }
        finally { IsLoading = false; }
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    private async Task ReloadAsync()
    {
        var mirrors = await _client.ListAsync(ImageMirrorTarget.Docker);
        Mirrors.Clear();
        foreach (var mirror in mirrors) Mirrors.Add(new ImageMirrorItemViewModel(mirror));
    }

    private void SetSelected(Guid id)
    {
        foreach (var item in Mirrors) item.IsSelected = item.Id == id;
    }

    private void RefreshSelection()
    {
        foreach (var item in Mirrors) item.RefreshSelection();
    }
}

public sealed partial class ImageMirrorItemViewModel(ImageMirrorDto mirror) : ObservableObject
{
    public Guid Id { get; } = mirror.Id;
    public string Name { get; } = mirror.Name;
    public string Endpoint { get; } = mirror.Endpoint;
    public bool IsDefault => Id == Guid.Empty;
    public bool CanRemove => !IsDefault;
    [ObservableProperty] private bool _isSelected = mirror.IsSelected;
    public void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));
}
