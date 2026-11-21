using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RelaxKonOS.App.Git.Views;

public sealed partial class GitToolTab(string kind, Control view, GitClientViewModel? session = null, string? path = null) : ObservableObject
{
    public string Kind { get; } = kind;
    public Control View { get; } = view;
    public GitClientViewModel? Session { get; } = session;
    public string? Path { get; } = path;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _canClose = true;
}
