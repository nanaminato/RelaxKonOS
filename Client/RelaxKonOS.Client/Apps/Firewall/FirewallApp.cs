using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Firewall;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Firewall;

/// <summary>Built-in Linux Server UFW editor.</summary>
public sealed class FirewallApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.firewall"), "Firewall", "0.2.0", "🧱", "Manage Linux Server UFW firewall rules",
        [AppPermissions.ServerFirewallRead, AppPermissions.ServerFirewallManage],
        ServerRequirements: new ApplicationServerRequirements(Platforms: [ApplicationPlatformNames.Linux], Capabilities: [ServerCapabilities.Firewall]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteFirewallClient)) as IRemoteFirewallClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.firewall.display_name"), new TextBlock { Text = LocalizedText.Get("firewall.login_required"), Margin = new Avalonia.Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new FirewallViewModel(client, session, context.Permissions);
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.firewall.display_name"), CreateView(viewModel), new Rect(70, 55, 1160, 760), Manifest.IconGlyph);
        viewModel.ShowPrivilegedHelperUnavailableAsync = problemCode => PrivilegedHelperUnavailableDialog.ShowAsync(context, window, problemCode);
        viewModel.RequestPasswordAsync = () => RequestPasswordAsync(context, window);
        viewModel.ShowRuleEditorAsync = editing => ShowRuleEditorAsync(context, window, viewModel, editing);
        _ = viewModel.StartAsync();
    }

    private static Control CreateView(FirewallViewModel vm)
    {
        var root = new DockPanel { Margin = new Avalonia.Thickness(18), LastChildFill = true, DataContext = vm };
        var refresh = new Button { Content = LocalizedText.Get("common.refresh"), Command = vm.RefreshCommand, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(refresh, Dock.Top); root.Children.Add(refresh);
        var status = new TextBlock { Margin = new Avalonia.Thickness(0, 0, 0, 12), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText))); DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);

        var settings = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 14) };
        settings.Children.Add(new TextBlock { Text = LocalizedText.Get("firewall.warning"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var settingsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom };
        settingsRow.Children.Add(new Button { Content = LocalizedText.Get("firewall.enable"), Command = vm.EnableCommand });
        settingsRow.Children.Add(new Button { Content = LocalizedText.Get("firewall.disable"), Command = vm.DisableCommand });
        settingsRow.Children.Add(ChoiceField(vm, nameof(vm.SelectedIncomingPolicy), vm.Policies, "firewall.default_incoming", 150));
        settingsRow.Children.Add(ChoiceField(vm, nameof(vm.SelectedOutgoingPolicy), vm.Policies, "firewall.default_outgoing", 150));
        settingsRow.Children.Add(new Button { Content = LocalizedText.Get("firewall.save_defaults"), Command = vm.SaveDefaultsCommand });
        settings.Children.Add(settingsRow);
        DockPanel.SetDock(settings, Dock.Top); root.Children.Add(settings);

        var rules = new DockPanel { DataContext = vm };
        var ruleActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Avalonia.Thickness(0, 0, 0, 10) };
        ruleActions.Children.Add(new Button { Content = LocalizedText.Get("firewall.rule.add"), Command = vm.ShowAddRuleEditorCommand });
        ruleActions.Children.Add(new Button { Content = LocalizedText.Get("firewall.rule.update"), Command = vm.ShowEditRuleEditorCommand });
        DockPanel.SetDock(ruleActions, Dock.Top); rules.Children.Add(ruleActions);

        var delete = new Button { Content = LocalizedText.Get("firewall.rule.delete"), Command = vm.DeleteRuleCommand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(delete, Dock.Bottom); rules.Children.Add(delete);
        rules.Children.Add(CreateRuleTable(vm));
        root.Children.Add(rules);
        return root;
    }

    private static DataGrid CreateRuleTable(FirewallViewModel vm)
    {
        var table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ItemsSource = vm.Rules
        };
        table.Columns.Add(Column("firewall.rule.number", nameof(FirewallRuleDto.Number), 70));
        table.Columns.Add(Column("firewall.rule.action", nameof(FirewallRuleDto.Action), 100));
        table.Columns.Add(Column("firewall.rule.direction", nameof(FirewallRuleDto.Direction), 100));
        table.Columns.Add(Column("firewall.rule.protocol", nameof(FirewallRuleDto.Protocol), 100));
        table.Columns.Add(Column("firewall.rule.address_family", nameof(FirewallRuleDto.AddressFamily), 120));
        table.Columns.Add(Column("firewall.rule.source", nameof(FirewallRuleDto.Source), 210));
        table.Columns.Add(Column("firewall.rule.destination", nameof(FirewallRuleDto.Destination), 210));
        table.Columns.Add(Column("firewall.rule.port", nameof(FirewallRuleDto.Port), 140));
        table.SelectionChanged += (_, _) => vm.SelectedRule = table.SelectedItem as FirewallRuleDto;
        return table;
    }

    private static Task ShowRuleEditorAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner, FirewallViewModel vm, bool editing) =>
        context.ShowDialogAsync<bool>(owner,
            LocalizedText.Get(editing ? "firewall.rule.edit_dialog_title" : "firewall.rule.add_dialog_title"), dialog =>
            {
                var content = new DockPanel { Margin = new Avalonia.Thickness(20), LastChildFill = true, DataContext = vm };
                var help = new TextBlock { Text = LocalizedText.Get("firewall.rule.help"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 12) };
                DockPanel.SetDock(help, Dock.Top);
                content.Children.Add(help);
                var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText)));
                status.Margin = new Avalonia.Thickness(0, 0, 0, 12);
                DockPanel.SetDock(status, Dock.Top);
                content.Children.Add(status);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
                var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
                cancel.Click += (_, _) => dialog.Cancel();
                var save = new Button { Content = LocalizedText.Get(editing ? "firewall.rule.update" : "firewall.rule.add"), Classes = { "primary" } };
                save.Click += async (_, _) =>
                {
                    var success = editing ? await vm.UpdateRuleAsync() : await vm.AddRuleAsync();
                    if (success) dialog.Close(true);
                };
                actions.Children.Add(cancel);
                actions.Children.Add(save);
                DockPanel.SetDock(actions, Dock.Bottom);
                content.Children.Add(actions);

                var fields = new StackPanel { Spacing = 12 };
                fields.Children.Add(ChoiceField(vm, nameof(vm.SelectedAction), vm.Actions, "firewall.rule.action", 360));
                fields.Children.Add(ChoiceField(vm, nameof(vm.SelectedDirection), vm.Directions, "firewall.rule.direction", 360));
                fields.Children.Add(ChoiceField(vm, nameof(vm.SelectedProtocol), vm.Protocols, "firewall.rule.protocol", 360));
                fields.Children.Add(TextField(vm, nameof(vm.Source), "firewall.rule.source", "firewall.rule.source_hint", 360, "firewall.rule.source_tooltip"));
                fields.Children.Add(TextField(vm, nameof(vm.Destination), "firewall.rule.destination", "firewall.rule.destination_hint", 360, "firewall.rule.destination_tooltip"));
                fields.Children.Add(TextField(vm, nameof(vm.Port), "firewall.rule.port", "firewall.rule.port_hint", 360));
                content.Children.Add(new ScrollViewer
                {
                    Content = fields,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                });
                return content;
            }, new RelaxKonOS.Core.Primitives.Size(460, 560));

    private static DataGridTextColumn Column(string headerKey, string property, double width) => new()
    {
        Header = LocalizedText.Get(headerKey), Binding = new Avalonia.Data.Binding(property), Width = new DataGridLength(width, DataGridLengthUnitType.Pixel)
    };

    private static Control ChoiceField(FirewallViewModel vm, string property, IReadOnlyList<FirewallOption> choices, string labelKey, double width)
    {
        var field = new StackPanel { Spacing = 4, Width = width, DataContext = vm, HorizontalAlignment = HorizontalAlignment.Left };
        field.Children.Add(new TextBlock { Text = LocalizedText.Get(labelKey) });
        var box = new ComboBox { ItemsSource = choices, DisplayMemberBinding = new Avalonia.Data.Binding(nameof(FirewallOption.Label)) };
        box.Bind(SelectingItemsControl.SelectedItemProperty, new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay });
        field.Children.Add(box);
        return field;
    }

    private static Control TextField(FirewallViewModel vm, string property, string labelKey, string hintKey, double width, string? tooltipKey = null)
    {
        var field = new StackPanel { Spacing = 4, Width = width, DataContext = vm, HorizontalAlignment = HorizontalAlignment.Left };
        var label = new TextBlock { Text = LocalizedText.Get(labelKey) };
        if (tooltipKey is not null) ToolTip.SetTip(label, LocalizedText.Get(tooltipKey));
        field.Children.Add(label);
        var box = new TextBox { PlaceholderText = LocalizedText.Get(hintKey) };
        box.Bind(TextBox.TextProperty, new Avalonia.Data.Binding(property) { Mode = Avalonia.Data.BindingMode.TwoWay });
        field.Children.Add(box);
        return field;
    }

    private static Task<string?> RequestPasswordAsync(AppContext context, RelaxKonOS.WindowManager.ManagedWindow owner) =>
        context.ShowDialogAsync<string?>(owner, LocalizedText.Get("firewall.password_dialog.title"), dialog =>
        {
            var password = new TextBox { PasswordChar = '•', PlaceholderText = LocalizedText.Get("firewall.password_placeholder") };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
            cancel.Click += (_, _) => dialog.Cancel();
            var confirm = new Button { Content = LocalizedText.Get("common.ok"), Classes = { "primary" } };
            confirm.Click += (_, _) => dialog.Close(password.Text);
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            return new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(20),
                Children =
                {
                    new TextBlock { Text = LocalizedText.Get("firewall.password_dialog.message"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    password,
                    actions,
                },
            };
        }, new RelaxKonOS.Core.Primitives.Size(420, 180));
}
