using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.App.ProcessGuardian;

public sealed class ProcessGuardianViews(Func<string, string> localize)
{
    public Control CreateWorkspace(ProcessGuardianViewModel vm)
    {
        var root = new DockPanel { Classes = { "builtin-app" }, Margin = new Avalonia.Thickness(18), LastChildFill = true, DataContext = vm };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(new Button { Content = localize("guardian.create.open"), Command = vm.OpenCreateWorkloadCommand, Classes = { "primary" } });
        var refresh = new Button { Content = localize("common.refresh"), Command = vm.RefreshCommand, MinWidth = 38 };
        Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);

        var notice = new Border
        {
            Classes = { "app-panel" },
            Padding = new Avalonia.Thickness(14, 10),
            Margin = new Avalonia.Thickness(0, 0, 0, 12),
            Child = new TextBlock { Text = localize("guardian.info"), TextWrapping = TextWrapping.Wrap }
        };
        DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);

        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush") };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var manage = new Button { Content = localize("guardian.install.manage"), Command = vm.ManageInstallationCommand, Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        manage.Bind(Control.IsVisibleProperty, new Avalonia.Data.Binding(nameof(vm.AgentNeedsInstallation)));
        DockPanel.SetDock(manage, Dock.Top); root.Children.Add(manage);
        root.Children.Add(CreateWorkloadTable(vm));
        return root;
    }

    private DataGrid CreateWorkloadTable(ProcessGuardianViewModel vm)
    {
        var table = new DataGrid
        {
            Classes = { "resource-table" },
            CanUserResizeColumns = true,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            DataContext = vm
        };
        // Avalonia DataGrid owns its item source property; binding the base ItemsControl
        // property leaves the grid visually empty even though Workloads is populated.
        table.ItemsSource = vm.Workloads;
        table.Columns.Add(TextColumn("guardian.table.name", nameof(GuardianWorkloadDto.Name), 140));
        table.Columns.Add(TextColumn("guardian.table.command", nameof(GuardianWorkloadDto.ExecutablePath), 220));
        table.Columns.Add(TextColumn("guardian.table.working_directory", nameof(GuardianWorkloadDto.WorkingDirectory), 180));
        table.Columns.Add(TextColumn("guardian.table.run_as", nameof(GuardianWorkloadDto.RunAs), 140));
        table.Columns.Add(TextColumn("guardian.table.pid", nameof(GuardianWorkloadDto.ProcessId), 80));
        table.Columns.Add(TextColumn("guardian.table.restarts", nameof(GuardianWorkloadDto.RestartCount), 80));
        table.Columns.Add(TextColumn("guardian.table.state", nameof(GuardianWorkloadDto.ActualState), 100));
        table.Columns.Add(new DataGridTemplateColumn
        {
            Header = localize("guardian.table.actions"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            CellTemplate = new FuncDataTemplate<GuardianWorkloadDto>((_, _) => CreateActionButtons(vm))
        });
        return table;
    }

    private DataGridTextColumn TextColumn(string headerKey, string property, double width) => new()
    {
        Header = localize(headerKey),
        Binding = new Avalonia.Data.Binding(property),
        Width = new DataGridLength(width, DataGridLengthUnitType.Pixel)
    };

    private Control CreateActionButtons(ProcessGuardianViewModel vm)
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

    private Button ActionButton(string textKey, System.Windows.Input.ICommand command)
    {
        var button = new Button { Content = localize(textKey), Command = command, Padding = new Avalonia.Thickness(4, 2) };
        button.Bind(Button.CommandParameterProperty, new Avalonia.Data.Binding());
        return button;
    }

    public Control CreateEditor(ProcessGuardianViewModel vm, Action cancelEditor,
        Func<Task> selectExecutable, Func<Task> selectWorkingDirectory)
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20), DataContext = vm };
        panel.Children.Add(new TextBlock { Text = localize("guardian.editor.intro"), TextWrapping = TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush") });
        panel.Children.Add(EditorField("guardian.create.name", "guardian.create.name.help", "guardian.create.name.example", nameof(vm.DefinitionName)));
        panel.Children.Add(EditorField("guardian.create.executable", "guardian.create.executable.help", "guardian.create.executable.example", nameof(vm.ExecutablePath), browse: selectExecutable));
        panel.Children.Add(EditorField("guardian.create.working_directory", "guardian.create.working_directory.help", "guardian.create.working_directory.example", nameof(vm.WorkingDirectory), browse: selectWorkingDirectory));
        panel.Children.Add(EditorField("guardian.create.arguments", "guardian.create.arguments.help", "guardian.create.arguments.example", nameof(vm.ArgumentsText), true));
        panel.Children.Add(EditorField("guardian.create.run_as", "guardian.create.run_as.help", "guardian.create.run_as.example", nameof(vm.RunAs)));
        var startup = new StackPanel { Spacing = 4 };
        var enabledOnBoot = new CheckBox { Content = localize("guardian.create.enabled_on_boot") };
        enabledOnBoot.Bind(ToggleButton.IsCheckedProperty, new Avalonia.Data.Binding(nameof(vm.EnabledOnBoot)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        startup.Children.Add(enabledOnBoot);
        startup.Children.Add(EditorHelp("guardian.create.enabled_on_boot.help"));
        panel.Children.Add(startup);

        // Keep the editor actions outside the scrollable form.  This makes Cancel and
        // Save consistently available at the lower-right corner for long workloads.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(20, 10, 20, 20) };
        var cancel = new Button { Content = localize("common.cancel") };
        cancel.Click += (_, _) => cancelEditor();
        actions.Children.Add(cancel);
        actions.Children.Add(new Button { Content = localize("guardian.create.submit"), Command = vm.CreateWorkloadCommand, Classes = { "primary" } });

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        // A horizontally-scrollable ScrollViewer measures its child with unlimited width.
        // That made the StackPanel (and its TextBoxes) grow to the longest help text instead
        // of the dialog width. Keep scrolling vertical-only so the form receives the available
        // width and the wrapped descriptions can lay out within it.
        root.Children.Add(new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        return root;
    }

    public Control CreateLogs(GuardianLogWindowViewModel vm)
    {
        var root = new DockPanel { Classes = { "builtin-app" }, Margin = new Avalonia.Thickness(16), DataContext = vm };
        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 8), [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush") };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var logs = new ListBox();
        logs.Bind(ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(vm.Lines)));
        root.Children.Add(logs);
        return root;
    }

    private Control EditorField(string labelKey, string helpKey, string exampleKey, string property, bool acceptsReturn = false, Func<Task>? browse = null)
    {
        var field = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        field.Children.Add(new TextBlock { Text = localize(labelKey), FontWeight = FontWeight.SemiBold });
        field.Children.Add(EditorHelp(helpKey));
        var input = new TextBox
        {
            PlaceholderText = localize(exampleKey),
            AcceptsReturn = acceptsReturn,
            MinHeight = acceptsReturn ? 88 : 0,
            TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            [!TextBox.TextProperty] = new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay }
        };
        if (browse is null)
        {
            field.Children.Add(input);
            return field;
        }

        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        inputRow.Children.Add(input);
        var browseButton = new Button { Content = localize("guardian.create.browse") };
        browseButton.Click += async (_, _) => await browse();
        Grid.SetColumn(browseButton, 1);
        inputRow.Children.Add(browseButton);
        field.Children.Add(inputRow);
        return field;
    }

    private TextBlock EditorHelp(string key) => new()
    {
        Text = localize(key),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush")
    };
}
