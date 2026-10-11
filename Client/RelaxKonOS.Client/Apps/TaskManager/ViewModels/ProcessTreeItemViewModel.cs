using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Client.Apps.TaskManager.ViewModels;

public sealed partial class ProcessTreeItemViewModel(ProcessInfoDto process) : ObservableObject, IDisposable
{
    [ObservableProperty] private ProcessInfoDto _process = process;
    [ObservableProperty] private int _depth;
    [ObservableProperty] private bool _hasChildren;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private IImage? _iconImage;
    [ObservableProperty] private bool _isIconLoaded;
    public bool IsDisposed { get; private set; }

    public Thickness Indent => new(Depth * 18, 0, 0, 0);
    public string ExpandGlyph => IsExpanded ? "⌄" : "›";
    public string FallbackIcon => IconFor(Process.Name);
    public bool HasIconImage => IconImage is not null;
    public event EventHandler? ExpandedStateChanged;

    public void Update(ProcessInfoDto process, int depth, bool hasChildren)
    {
        Process = process;
        Depth = depth;
        HasChildren = hasChildren;
        OnPropertyChanged(nameof(Indent));
        OnPropertyChanged(nameof(FallbackIcon));
        ToggleExpandedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasChildren))]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    partial void OnDepthChanged(int value) => OnPropertyChanged(nameof(Indent));
    partial void OnIconImageChanged(IImage? value) => OnPropertyChanged(nameof(HasIconImage));
    partial void OnIsExpandedChanged(bool value) { OnPropertyChanged(nameof(ExpandGlyph)); ExpandedStateChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnHasChildrenChanged(bool value) => ToggleExpandedCommand.NotifyCanExecuteChanged();

    public void Dispose()
    {
        IsDisposed = true;
        (IconImage as IDisposable)?.Dispose();
        IconImage = null;
    }

    private static string IconFor(string name)
    {
        var process = name.ToLowerInvariant();
        if (process.Contains("chrome") || process.Contains("firefox") || process.Contains("edge") || process.Contains("browser")) return "◉";
        if (process.Contains("bash") || process.Contains("zsh") || process.Contains("shell") || process.Contains("terminal") || process is "sh" or "cmd" or "pwsh") return "›_";
        if (process.Contains("docker") || process.Contains("containerd") || process.Contains("podman")) return "⬡";
        if (process.Contains("redis") || process.Contains("postgres") || process.Contains("mysql") || process.Contains("mongo")) return "▤";
        if (process.Contains("system") || process.Contains("service") || process.Contains("daemon") || process.Contains("sshd") || process.Contains("nginx")) return "⚙";
        if (process.Contains("dotnet") || process.Contains("java") || process.Contains("node")) return "◆";
        return "◼";
    }
}
