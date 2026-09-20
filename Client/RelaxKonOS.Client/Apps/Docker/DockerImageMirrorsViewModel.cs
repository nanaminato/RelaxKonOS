using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.ImageMirrors;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>Docker Hub mirror selection belongs beside image pulls, not among desktop settings.</summary>
public sealed partial class DockerImageMirrorsViewModel(IDockerImageMirrorClient client) : ObservableObject
{
    public ObservableCollection<DockerImageMirrorItemViewModel> Mirrors { get; } = [];
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newEndpoint = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = string.Empty;
    public bool HasStatus => StatusText.Length > 0;

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = string.Empty;
        try { await ReloadAsync(); }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.mirrors.load_failed", exception.Message); }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewEndpoint))
        {
            StatusText = LocalizedText.Get("docker.mirrors.required");
            return;
        }
        IsLoading = true;
        try
        {
            await client.CreateAsync(new CreateImageMirrorRequest(NewName.Trim(), NewEndpoint.Trim()));
            NewName = NewEndpoint = string.Empty;
            await ReloadAsync();
            StatusText = LocalizedText.Get("docker.mirrors.added");
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.mirrors.save_failed", exception.Message); }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SelectAsync(DockerImageMirrorItemViewModel? mirror)
    {
        if (mirror is null || mirror.IsSelected || IsLoading) return;
        IsLoading = true;
        try
        {
            await client.SelectAsync(mirror.IsDefault ? null : mirror.Id);
            foreach (var item in Mirrors) item.IsSelected = item.Id == mirror.Id;
            StatusText = mirror.IsDefault ? LocalizedText.Get("docker.mirrors.default_selected")
                : LocalizedText.Format("docker.mirrors.selected", mirror.Name);
        }
        catch (Exception exception)
        {
            await ReloadAsync();
            StatusText = LocalizedText.Format("docker.mirrors.select_failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task RemoveAsync(DockerImageMirrorItemViewModel? mirror)
    {
        if (mirror is null || mirror.IsDefault || IsLoading) return;
        IsLoading = true;
        try
        {
            await client.DeleteAsync(mirror.Id);
            Mirrors.Remove(mirror);
            if (mirror.IsSelected) SetSelected(Guid.Empty);
            StatusText = LocalizedText.Get("docker.mirrors.removed");
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("docker.mirrors.remove_failed", exception.Message); }
        finally { IsLoading = false; }
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    private async Task ReloadAsync()
    {
        var mirrors = await client.ListAsync();
        Mirrors.Clear();
        foreach (var mirror in mirrors) Mirrors.Add(new DockerImageMirrorItemViewModel(mirror));
    }

    private void SetSelected(Guid id)
    {
        foreach (var item in Mirrors) item.IsSelected = item.Id == id;
    }
}

public sealed partial class DockerImageMirrorItemViewModel(ImageMirrorDto mirror) : ObservableObject
{
    public Guid Id { get; } = mirror.Id;
    public string Name { get; } = mirror.Name;
    public string Endpoint { get; } = mirror.Endpoint;
    public bool IsDefault => Id == Guid.Empty;
    public bool CanRemove => !IsDefault;
    [ObservableProperty] private bool _isSelected = mirror.IsSelected;
}
