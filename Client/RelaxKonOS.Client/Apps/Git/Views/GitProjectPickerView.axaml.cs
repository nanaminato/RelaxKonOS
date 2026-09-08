using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Client.Apps.Git.Views;

/// <summary>项目选择视图：列出已注册项目供直接打开，或新打开一个远程文件夹作为新项目。
/// 注意：此控件在 GitClientWorkspace.axaml 中以 XAML 方式声明 → 使用无参构造函数 → 依赖继承的 DataContext，不要依赖构造函数注入 VM。
/// 所有按钮 Click 处理器统一走 DataContext（它与父级 GitClientWorkspace 的 GitClientViewModel 一致），
/// 并在状态栏（VM.StatusText）写入 [TRACE] 日志，便于诊断点击无反应类问题。</summary>
internal partial class GitProjectPickerView : UserControl
{
    public GitProjectPickerView() => InitializeComponent();

    // 构造函数注入（代码创建时使用）—— 不强制要求；实际运行时都走继承的 DataContext。
    public GitProjectPickerView(GitClientViewModel vm) : this() { DataContext = vm; }

    private GitClientViewModel? GetVm(object? sender)
    {
        // 优先从事件源取 DataContext（Avalonia 会传递继承链），再退回到本控件
        if (sender is Control c && c.DataContext is GitClientViewModel vm1) return vm1;
        if (DataContext is GitClientViewModel vm2) return vm2;
        return null;
    }

    /// <summary>Writes [GitPicker] logs to Debug Output only — does not touch user-facing StatusText.
    /// StatusText is owned by the ViewModel and set via LocalizedText.Get/Format for localization.</summary>
    private static void Log(GitClientViewModel? vm, string message)
    {
        _ = vm; // parameter retained for call-site stability; intentionally not used.
        System.Diagnostics.Debug.WriteLine($"[GitPicker] {message}");
    }

    private void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var vm = GetVm(sender);
        if (sender is Button { Tag: GitRepositoryDto repo })
        {
            Log(vm, $"OpenProject_Click: name={repo.Name} id={repo.Id} CanExecute?={vm?.OpenProjectCommand.CanExecute(repo)}");
            if (vm is null) { Log(null, "VM is null → 忽略点击（DataContext 尚未到位）"); return; }
            if (!vm.OpenProjectCommand.CanExecute(repo))
            {
                Log(vm, $"CanExecute=false (IsBusy={vm.IsBusy}, IsPickerMode={vm.IsPickerMode})");
                return;
            }
            try
            {
                vm.OpenProjectCommand.Execute(repo);
                Log(vm, "OpenProjectCommand.Execute 已触发");
            }
            catch (Exception ex)
            {
                Log(vm, $"OpenProjectCommand 抛异常：{ex.GetType().Name} {ex.Message}");
            }
        }
        else
        {
            Log(vm, $"OpenProject_Click: sender Tag is NOT GitRepositoryDto → ignored (sender={sender}, Tag={(sender as Button)?.Tag})");
        }
    }
}
