using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.ImageMirrors;

namespace RelaxKonOS.App.Docker;

/// <summary>Docker Hub mirror selection belongs beside image pulls, not among desktop settings.</summary>
public sealed partial class DockerImageMirrorsViewModel(IServerDockerImageMirrors client, DockerText text) : ObservableObject
{
    public ObservableCollection<DockerImageMirrorItemViewModel> Mirrors { get; } = [];
    public DockerText Labels => new(text.Get);
    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newEndpoint = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = string.Empty;
    public bool HasStatus => StatusText.Length > 0;

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Labels));
        foreach (var item in Mirrors) item.RefreshLocalization();
    }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = string.Empty;
        try { await ReloadAsync(); }
        catch (Exception exception) { StatusText = text.Format("docker.mirrors.load_failed", exception.Message); }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewEndpoint))
        {
            StatusText = text.Get("docker.mirrors.required");
            return;
        }
        IsLoading = true;
        try
        {
            await client.CreateAsync(new CreateImageMirrorRequest(NewName.Trim(), NewEndpoint.Trim()));
            NewName = NewEndpoint = string.Empty;
            await ReloadAsync();
            StatusText = text.Get("docker.mirrors.added");
        }
        catch (Exception exception) { StatusText = text.Format("docker.mirrors.save_failed", exception.Message); }
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
            StatusText = mirror.IsDefault ? text.Get("docker.mirrors.default_selected")
                : text.Format("docker.mirrors.selected", mirror.Name);
        }
        catch (Exception exception)
        {
            try { await ReloadAsync(); }
            catch (Exception) { /* Keep the previously observed list when the session or grant has changed. */ }
            StatusText = text.Format("docker.mirrors.select_failed", exception.Message);
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
            StatusText = text.Get("docker.mirrors.removed");
        }
        catch (Exception exception) { StatusText = text.Format("docker.mirrors.remove_failed", exception.Message); }
        finally { IsLoading = false; }
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    private async Task ReloadAsync()
    {
        var mirrors = await client.ListAsync();
        Mirrors.Clear();
        foreach (var mirror in mirrors) Mirrors.Add(new DockerImageMirrorItemViewModel(mirror, text));
    }

    private void SetSelected(Guid id)
    {
        foreach (var item in Mirrors) item.IsSelected = item.Id == id;
    }
}

public sealed partial class DockerImageMirrorItemViewModel(ImageMirrorDto mirror, DockerText text) : ObservableObject
{
    public DockerText Labels => new(text.Get);
    internal void RefreshLocalization() => OnPropertyChanged(nameof(Labels));
    public Guid Id { get; } = mirror.Id;
    public string Name { get; } = mirror.Name;
    public string Endpoint { get; } = mirror.Endpoint;
    public bool IsDefault => Id == Guid.Empty;
    public bool CanRemove => !IsDefault;
    [ObservableProperty] private bool _isSelected = mirror.IsSelected;
}
