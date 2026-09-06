using Avalonia.Controls;
using Avalonia.Interactivity;
using Client.Localization;
using Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;

namespace Client.Views.Shell;

/// <summary>桌面显示配置对话框。</summary>
public partial class DesktopDisplayDialogs : UserControl
{
    private ShellSettings _settings = null!;
    private Func<Task> _saveAsync = () => Task.CompletedTask;
    private Action<bool> _close = _ => { };
    private readonly bool _isFirstTime;
    private DesktopDisplayEditBuffer _buffer = null!;

    /// <summary>供 Avalonia 运行时 XAML 加载器和设计器创建控件。</summary>
    public DesktopDisplayDialogs()
    {
        InitializeComponent();
    }

    /// <summary>创建桌面显示配置对话框。</summary>
    public DesktopDisplayDialogs(
        ShellSettings settings,
        ApplicationManager applications,
        Func<Task> saveAsync,
        Action<bool> close,
        bool isFirstTime)
        : this()
    {
        _settings = settings;
        _saveAsync = saveAsync;
        _close = close;
        _isFirstTime = isFirstTime;
        _buffer = new DesktopDisplayEditBuffer(settings, applications);

        InitializeDialog();
    }

    private void InitializeDialog()
    {
        WelcomeHeading.IsVisible = _isFirstTime;
        DescriptionText.Text = _isFirstTime
            ? T("shell.desktop_display.welcome_description", "Choose what appears on your desktop. You can change this later from the desktop context menu or with Ctrl+Shift+D.")
            : T("shell.desktop_display.description", "Choose what appears on your desktop. Your preferences are saved to this workspace and sync across devices.");

        ShowAppsToggle.IsChecked = _buffer.ShowBuiltInApps;
        AllAppsRadio.IsChecked = _buffer.ShowAllBuiltInApps;
        CustomAppsRadio.IsChecked = !_buffer.ShowAllBuiltInApps;
        ShowFilesToggle.IsChecked = _buffer.ShowServerDesktopFiles;
        ShowShortcutsToggle.IsChecked = _buffer.ShowServerDesktopShortcuts;
        CancelOrSkipButton.Content = _isFirstTime ? T("common.skip", "Skip") : T("common.cancel", "Cancel");
        SaveButton.Content = _isFirstTime
            ? T("shell.desktop_display.get_started", "Get started")
            : T("common.save", "Save");

        foreach (var item in _buffer.AppItems)
        {
            var appCheckBox = new CheckBox
            {
                Content = $"{item.IconGlyph}  {item.DisplayName}",
                IsChecked = item.IsVisible,
                Padding = new Avalonia.Thickness(4, 2),
            };
            appCheckBox.IsCheckedChanged += (_, _) => item.IsVisible = appCheckBox.IsChecked == true;
            AppList.Children.Add(appCheckBox);
        }

        UpdateAppsControls();
    }

    private void ShowAppsToggle_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        _buffer.ShowBuiltInApps = ShowAppsToggle.IsChecked == true;
        UpdateAppsControls();
    }

    private void AllAppsRadio_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (AllAppsRadio.IsChecked == true)
        {
            _buffer.ShowAllBuiltInApps = true;
            UpdateAppsControls();
        }
    }

    private void CustomAppsRadio_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (CustomAppsRadio.IsChecked == true)
        {
            _buffer.ShowAllBuiltInApps = false;
            UpdateAppsControls();
        }
    }

    private void UpdateAppsControls()
    {
        AllAppsRadio.IsEnabled = _buffer.ShowBuiltInApps;
        CustomAppsRadio.IsEnabled = _buffer.ShowBuiltInApps;
        AppListViewer.IsEnabled = _buffer.ShowBuiltInApps && !_buffer.ShowAllBuiltInApps;
    }

    private void CancelOrSkipButton_OnClick(object? sender, RoutedEventArgs e) => _close(false);

    private async void SaveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _buffer.ShowServerDesktopFiles = ShowFilesToggle.IsChecked == true;
        _buffer.ShowServerDesktopShortcuts = ShowShortcutsToggle.IsChecked == true;
        _buffer.ApplyTo(_settings);
        await _saveAsync();
        _close(true);
    }

    private static string T(string key, string fallback) => LocalizedText.Get(key, fallback);
}

/// <summary>编辑缓冲区：对话框工作期间持有一份副本，取消时不污染 ShellSettings。</summary>
internal sealed class DesktopDisplayEditBuffer
{
    public bool ShowBuiltInApps { get; set; }
    public bool ShowServerDesktopFiles { get; set; }
    public bool ShowServerDesktopShortcuts { get; set; }

    /// <summary>VisibleAppIds 为空 = true 代表“显示全部”。</summary>
    public bool ShowAllBuiltInApps { get; set; }

    public List<AppVisibilityItem> AppItems { get; } = [];

    public DesktopDisplayEditBuffer(ShellSettings settings, ApplicationManager applications)
    {
        ShowBuiltInApps = settings.ShowBuiltInApps;
        ShowServerDesktopFiles = settings.ShowServerDesktopFiles;
        ShowServerDesktopShortcuts = settings.ShowServerDesktopShortcuts;

        var visibleAppIds = new HashSet<string>(settings.VisibleAppIds, StringComparer.Ordinal);
        ShowAllBuiltInApps = visibleAppIds.Count == 0;

        var compatible = applications.Registered
            .Where(app => applications.GetManifest(app.Id) is { } manifest
                          && applications.EvaluateCompatibility(manifest).IsCompatible)
            .ToList();

        foreach (var app in compatible)
        {
            AppItems.Add(new AppVisibilityItem(app)
            {
                IsVisible = ShowAllBuiltInApps || visibleAppIds.Contains(app.Id.Value),
            });
        }
    }

    public void ApplyTo(ShellSettings settings)
    {
        settings.ShowBuiltInApps = ShowBuiltInApps;
        settings.ShowServerDesktopFiles = ShowServerDesktopFiles;
        settings.ShowServerDesktopShortcuts = ShowServerDesktopShortcuts;

        settings.VisibleAppIds = !ShowBuiltInApps || ShowAllBuiltInApps
            ? []
            : AppItems.Where(x => x.IsVisible).Select(x => x.App.Id.Value).ToList();
    }
}

/// <summary>应用可见性勾选项。</summary>
internal sealed partial class AppVisibilityItem : ObservableObject
{
    public ApplicationInfo App { get; }
    public string DisplayName => App.DisplayName;
    public string IconGlyph => string.IsNullOrWhiteSpace(App.IconGlyph) ? "📦" : App.IconGlyph;

    [ObservableProperty] private bool _isVisible;

    public AppVisibilityItem(ApplicationInfo app) => App = app;
}
