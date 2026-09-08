using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ProcessGuardian;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ProcessGuardian;

/// <summary>Built-in UI for workloads supervised by the separately installed Guardian Agent.</summary>
public sealed class ProcessGuardianApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.processguardian"), "Process Guardian", "0.1.0", "🛡", "View RelaxKonOS Guardian Agent workloads", [AppPermissions.ServerGuardianRead, AppPermissions.ServerGuardianManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IProcessGuardianClient)) as IProcessGuardianClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.processguardian.display_name"),
                new TextBlock { Text = LocalizedText.Get("guardian.login_required"), Margin = new Avalonia.Thickness(24), TextWrapping = TextWrapping.Wrap },
                new Rect(200, 160, 460, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var viewModel = new ProcessGuardianViewModel(client, session);
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.processguardian.display_name"), CreateView(viewModel), new Rect(80, 60, 1240, 700), Manifest.IconGlyph);
        viewModel.ShowPrivilegedHelperUnavailableAsync = problemCode => PrivilegedHelperUnavailableDialog.ShowAsync(context, window, problemCode);
        viewModel.RequestAdministratorApprovalAsync = () => RequestAdministratorApprovalAsync(context, window, session);
        viewModel.ShowEditorAsync = async isEdit =>
        {
            await context.ShowDialogAsync<bool>(window,
                LocalizedText.Get(isEdit ? "guardian.editor.edit_title" : "guardian.editor.create_title"), dialog =>
                {
                    viewModel.CloseEditorAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return CreateEditorView(viewModel, dialog);
                }, new RelaxKonOS.Core.Primitives.Size(640, 650));
        };
        viewModel.ShowLogsAsync = workload =>
        {
            var logViewModel = new GuardianLogWindowViewModel(session, workload);
            var logWindow = context.ShowWindow(logViewModel.Title, CreateLogsView(logViewModel), new Rect(170, 120, 800, 520), Manifest.IconGlyph);
            logWindow.CloseRequested += (_, _) => _ = logViewModel.DisposeAsync();
            _ = logViewModel.StartAsync();
            return Task.CompletedTask;
        };
        _ = viewModel.StartAsync();
    }

    private static Control CreateView(ProcessGuardianViewModel vm)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(18), LastChildFill = true, DataContext = vm };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(new Button { Content = LocalizedText.Get("guardian.create.open"), Command = vm.OpenCreateWorkloadCommand, Classes = { "primary" } });
        var refresh = new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand, MinWidth = 38 };
        Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);

        var notice = new Border
        {
            Background = ThemeBrushes.Get("AppBackgroundBrush"),
            Padding = new Avalonia.Thickness(14, 10),
            CornerRadius = new CornerRadius(3),
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = LocalizedText.Get("guardian.info"), TextWrapping = TextWrapping.Wrap }
        };
        DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);

        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get("TextSecondaryBrush") };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        root.Children.Add(CreateWorkloadTable(vm));
        return root;
    }

    private static DataGrid CreateWorkloadTable(ProcessGuardianViewModel vm)
    {
        var table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            DataContext = vm
        };
        // Avalonia DataGrid owns its item source property; binding the base ItemsControl
        // property leaves the grid visually empty even though Workloads is populated.
        table.ItemsSource = vm.Workloads;
        table.Columns.Add(TextColumn("guardian.table.name", nameof(GuardianWorkloadDto.Name), "140"));
        table.Columns.Add(TextColumn("guardian.table.command", nameof(GuardianWorkloadDto.ExecutablePath), "220"));
        table.Columns.Add(TextColumn("guardian.table.working_directory", nameof(GuardianWorkloadDto.WorkingDirectory), "180"));
        table.Columns.Add(TextColumn("guardian.table.run_as", nameof(GuardianWorkloadDto.RunAs), "140"));
        table.Columns.Add(TextColumn("guardian.table.pid", nameof(GuardianWorkloadDto.ProcessId), "80"));
        table.Columns.Add(TextColumn("guardian.table.restarts", nameof(GuardianWorkloadDto.RestartCount), "80"));
        table.Columns.Add(TextColumn("guardian.table.state", nameof(GuardianWorkloadDto.ActualState), "100"));
        table.Columns.Add(new DataGridTemplateColumn
        {
            Header = LocalizedText.Get("guardian.table.actions"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            CellTemplate = new FuncDataTemplate<GuardianWorkloadDto>((_, _) => CreateActionButtons(vm))
        });
        return table;
    }

    private static DataGridTextColumn TextColumn(string headerKey, string property, string width) => new()
    {
        Header = LocalizedText.Get(headerKey),
        Binding = new Avalonia.Data.Binding(property),
        Width = new DataGridLength(double.Parse(width, System.Globalization.CultureInfo.InvariantCulture), DataGridLengthUnitType.Pixel)
    };

    private static Control CreateActionButtons(ProcessGuardianViewModel vm)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(4, 2) };
        actions.Children.Add(ActionButton("guardian.action.edit", vm.EditWorkloadCommand));
        actions.Children.Add(ActionButton("guardian.logs.show", vm.OpenLogsCommand));
        actions.Children.Add(ActionButton("guardian.action.start", vm.StartWorkloadCommand));
        actions.Children.Add(ActionButton("guardian.action.stop", vm.StopWorkloadCommand));
        actions.Children.Add(ActionButton("guardian.action.restart", vm.RestartWorkloadCommand));
        actions.Children.Add(ActionButton("guardian.action.delete", vm.DeleteWorkloadCommand));
        return actions;
    }

    private static Button ActionButton(string textKey, System.Windows.Input.ICommand command)
    {
        var button = new Button { Content = LocalizedText.Get(textKey), Command = command, Padding = new Avalonia.Thickness(4, 2) };
        button.Bind(Button.CommandParameterProperty, new Avalonia.Data.Binding());
        return button;
    }

    private static Control CreateEditorView(ProcessGuardianViewModel vm, RelaxKonOS.WindowManager.ModalDialog<bool> dialog)
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20), DataContext = vm };
        panel.Children.Add(new TextBlock { Text = LocalizedText.Get("guardian.editor.intro"), TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get("TextSecondaryBrush") });
        panel.Children.Add(EditorField("guardian.create.name", "guardian.create.name.help", "guardian.create.name.example", nameof(vm.DefinitionName)));
        panel.Children.Add(EditorField("guardian.create.executable", "guardian.create.executable.help", "guardian.create.executable.example", nameof(vm.ExecutablePath)));
        panel.Children.Add(EditorField("guardian.create.working_directory", "guardian.create.working_directory.help", "guardian.create.working_directory.example", nameof(vm.WorkingDirectory)));
        panel.Children.Add(EditorField("guardian.create.arguments", "guardian.create.arguments.help", "guardian.create.arguments.example", nameof(vm.ArgumentsText), true));
        panel.Children.Add(EditorField("guardian.create.run_as", "guardian.create.run_as.help", "guardian.create.run_as.example", nameof(vm.RunAs)));
        var startup = new StackPanel { Spacing = 4 };
        var enabledOnBoot = new CheckBox { Content = LocalizedText.Get("guardian.create.enabled_on_boot") };
        enabledOnBoot.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.EnabledOnBoot)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        startup.Children.Add(enabledOnBoot);
        startup.Children.Add(EditorHelp("guardian.create.enabled_on_boot.help"));
        panel.Children.Add(startup);

        // Keep the editor actions outside the scrollable form.  This makes Cancel and
        // Save consistently available at the lower-right corner for long workloads.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(20, 10, 20, 20) };
        var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
        cancel.Click += (_, _) => dialog.Cancel();
        actions.Children.Add(cancel);
        actions.Children.Add(new Button { Content = LocalizedText.Get("guardian.create.submit"), Command = vm.CreateWorkloadCommand, Classes = { "primary" } });

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = panel });
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        return root;
    }

    private static Control CreateLogsView(GuardianLogWindowViewModel vm)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(16), DataContext = vm };
        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 8), Foreground = ThemeBrushes.Get("TextSecondaryBrush") };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var logs = new ListBox();
        logs.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(vm.Lines)));
        root.Children.Add(logs);
        return root;
    }

    private static Control EditorField(string labelKey, string helpKey, string exampleKey, string property, bool acceptsReturn = false)
    {
        var field = new StackPanel { Spacing = 4 };
        field.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey), FontWeight = FontWeight.SemiBold });
        field.Children.Add(EditorHelp(helpKey));
        field.Children.Add(new TextBox
        {
            PlaceholderText = LocalizedText.Get(exampleKey),
            AcceptsReturn = acceptsReturn,
            MinHeight = acceptsReturn ? 88 : 0,
            TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
            [!TextBox.TextProperty] = new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay }
        });
        return field;
    }

    private static TextBlock EditorHelp(string key) => new()
    {
        Text = LocalizedText.Get(key),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrushes.Get("TextSecondaryBrush")
    };

    private static Task<RunAsAdministratorApproval?> RequestAdministratorApprovalAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, IAuthSession session) =>
        context.ShowDialogAsync<RunAsAdministratorApproval?>(owner, LocalizedText.Get("guardian.admin_approval.title"), dialog =>
        {
            var username = new TextBox
            {
                Text = session.CurrentServer?.Platform == PlatformKind.Windows ? "Administrator" : "root",
                PlaceholderText = LocalizedText.Get("guardian.admin_approval.username"),
            };
            var password = new TextBox { PasswordChar = '•', PlaceholderText = LocalizedText.Get("guardian.admin_approval.password") };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
            cancel.Click += (_, _) => dialog.Cancel();
            var confirm = new Button { Content = LocalizedText.Get("common.ok"), Classes = { "primary" } };
            confirm.Click += (_, _) => dialog.Close(new RunAsAdministratorApproval(username.Text?.Trim() ?? string.Empty, password.Text ?? string.Empty));
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            return new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = LocalizedText.Get("guardian.admin_approval.message"), TextWrapping = TextWrapping.Wrap },
                    username,
                    password,
                    actions,
                },
            };
        }, new RelaxKonOS.Core.Primitives.Size(460, 230));
}
