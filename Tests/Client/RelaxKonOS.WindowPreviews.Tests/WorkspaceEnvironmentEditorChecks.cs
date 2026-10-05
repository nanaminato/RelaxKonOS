using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Apps.Settings.Views.Pages;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Protocol.Settings;

internal static class WorkspaceEnvironmentEditorChecks
{
    public static void Run(ShellSettings settings)
    {
        var client = DispatchProxy.Create<IWorkspaceEnvironmentClient, WorkspaceEnvironmentClientStub>();
        var stub = (WorkspaceEnvironmentClientStub)client;
        using var vm = new WorkspaceEnvironmentEditorViewModel(client, DispatchProxy.Create<IAuthSession, LanguageSessionStub>());
        vm.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.VariableName = "EMPTY"; vm.VariableValue = ""; vm.StageSetCommand.Execute(null);
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(stub.Update!.Change.Changes.Single() is { Operation: EnvironmentMutationKind.Set, Value: "" } && !vm.HasDraft, "Empty value is saved, not deleted.");
        vm.VariableName = "PATH"; vm.VariableValue = ":/one:/one:"; vm.StageSetCommand.Execute(null);
        Check(vm.NeedsHighImpactConfirmation && !vm.ApplyCommand.CanExecute(null), "PATH requires confirmation.");
        vm.ConfirmHighImpact = true; stub.Fail = true;
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(vm.HasDraft && !vm.CanEdit && !vm.ApplyCommand.CanExecute(null), "Unknown write retains draft and blocks replay.");
        stub.Fail = false;
        vm.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(!vm.HasDraft && vm.CanEdit, "Explicit reload recovers the snapshot.");
        vm.AppendPath = false;
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(stub.Update!.Change.Changes.Count == 0 && stub.Update.PathMode == EnvironmentPathMode.Replace, "Mode-only edit creates no artificial variable.");
        vm.VariableName = "EMPTY"; vm.StageDeleteCommand.Execute(null);
        vm.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(stub.Update!.Change.Changes.Single().Operation == EnvironmentMutationKind.Delete, "Deletion is explicit.");
        var language = settings.Language;
        var view = new WorkspaceEnvironmentEditorView { DataContext = vm };
        var window = new Window { Width = 640, Height = 480, Content = view };
        window.Show();
        foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            settings.Language = culture; Dispatcher.UIThread.RunJobs();
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, "Workspace editor does not require horizontal scrolling.");
            var apply = view.GetVisualDescendants().OfType<Button>().Single(button => button.Command == vm.ApplyCommand);
            var point = apply.TranslatePoint(default, window)!.Value;
            Check(point.Y >= 0 && point.Y + apply.Bounds.Height <= window.ClientSize.Height, "Apply remains visible in a short window.");
        }
        window.Close(); settings.Language = language; Dispatcher.UIThread.RunJobs();
        vm.Dispose();
        Check(vm.Variables.Count == 0 && vm.VariableValue == "" && !vm.HasDraft && !vm.CanEdit, "Closing clears values and drafts.");
        Console.WriteLine("PASS: Workspace environment empty/delete, PATH confirmation, uncertain-write no replay, reload, mode-only edits and sensitive cleanup.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
public class WorkspaceEnvironmentClientStub : DispatchProxy
{
    private readonly WorkspaceEnvironmentConnection _connection = new(new("test", Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid());
    public WorkspaceEnvironmentUpdate? Update;
    public bool Fail;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "CaptureConnection" => _connection,
        "IsCurrent" => true,
        "ReadAsync" => Task.FromResult(new WorkspaceEnvironmentSnapshot(_connection.WorkspaceId, "1", DateTimeOffset.UtcNow, [], EnvironmentPathMode.Append)),
        "SaveAsync" => Save((WorkspaceEnvironmentUpdate)args![1]!),
        _ => throw new NotSupportedException(method.Name)
    };
    private Task<WorkspaceEnvironmentSnapshot> Save(WorkspaceEnvironmentUpdate update)
    {
        Update = update;
        if (Fail) return Task.FromException<WorkspaceEnvironmentSnapshot>(new HttpRequestException("Disconnect"));
        return Task.FromResult(new WorkspaceEnvironmentSnapshot(_connection.WorkspaceId, "2", DateTimeOffset.UtcNow, [], update.PathMode));
    }
}
