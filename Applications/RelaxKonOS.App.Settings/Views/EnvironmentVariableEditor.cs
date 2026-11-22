using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.App.Settings.Views;

public static class EnvironmentVariableEditor
{
    public static Control Create(EnvironmentVariable? existing, bool windows, Func<string, string> labels,
        Func<Task<string?>> browseFolder, Action cancelEdit, Action<EnvironmentMutation> complete)
    {
        var name = new TextBox
        {
            Text = existing?.Name ?? "",
            MaxLength = EnvironmentValidation.MaximumNameLength,
            IsReadOnly = existing is not null,
        };
        var isPath = existing?.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase) == true;
        var value = new TextBox
        {
            Text = existing?.RawValue ?? "",
            AcceptsReturn = false,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxLength = EnvironmentValidation.MaximumValueLength,
            MinHeight = 32,
        };
        var pathEditor = new EnvironmentPathEditor(existing?.RawValue ?? "", isPath, windows ? ";" : ":");
        var pathEntry = new TextBox { MaxLength = EnvironmentValidation.MaximumValueLength };
        var pathList = new ListBox { ItemsSource = pathEditor.Entries, MinHeight = 80, SelectionMode = SelectionMode.Single };
        pathList.Classes.Add("windows-path-editor");
        pathList.Bind(SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(EnvironmentPathEditor.SelectedEntry)) { Source = pathEditor, Mode = BindingMode.TwoWay });
        pathEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EnvironmentPathEditor.SelectedEntry) && pathEditor.SelectedEntry is { } entry)
                pathList.ScrollIntoView(entry);
        };
        pathList.ItemTemplate = new FuncDataTemplate<EnvironmentPathEntry>((_, _) =>
        {
            var text = new TextBlock { Margin = new Thickness(8, 4), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            text.Bind(TextBlock.TextProperty, new Binding(nameof(EnvironmentPathEntry.Value)));
            return text;
        });
        // This dialog is hosted in its own window, so its selected state needs a local style.
        pathList.Styles.Add(new Style(selector => selector.Is<ListBoxItem>().Class(":selected"))
        {
            Setters =
            {
                new Setter(ListBoxItem.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SelectionBackgroundBrush")),
                new Setter(ListBoxItem.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("FocusBorderBrush")),
                new Setter(ListBoxItem.BorderThicknessProperty, new Thickness(1)),
            },
        });
        pathEntry.Bind(TextBox.TextProperty,
            new Binding("SelectedEntry.Value") { Source = pathEditor, Mode = BindingMode.TwoWay });
        pathEntry.Bind(Control.IsEnabledProperty,
            new Binding(nameof(EnvironmentPathEditor.HasSelection)) { Source = pathEditor });
        var pathButtons = new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(0, 0, 0, 16),
        };
        void AddPathButton(string key, Action action)
        {
            var button = new Button { Content = labels(key), MinWidth = 82 };
            button.Click += (_, _) => action();
            pathButtons.Children.Add(button);
        }
        AddPathButton("common.new", () => { pathEditor.Add(); pathEntry.Focus(); });
        AddPathButton("settings.environment.edit", () => { if (pathEditor.HasSelection) pathEntry.Focus(); });
        AddPathButton("common.delete", pathEditor.RemoveSelected);
        AddPathButton("settings.environment.path_up", () =>
        {
            pathEditor.MoveSelected(-1);
            if (pathEditor.SelectedEntry is { } entry) pathList.ScrollIntoView(entry);
        });
        AddPathButton("settings.environment.path_down", () =>
        {
            pathEditor.MoveSelected(1);
            if (pathEditor.SelectedEntry is { } entry) pathList.ScrollIntoView(entry);
        });
        var browse = new Button { Content = labels("settings.environment.browse"), IsEnabled = !isPath };
        if (isPath)
            browse.Bind(Control.IsEnabledProperty,
                new Binding(nameof(EnvironmentPathEditor.HasSelection)) { Source = pathEditor });
        browse.Click += async (_, _) =>
        {
            var selected = await browseFolder();
            if (string.IsNullOrWhiteSpace(selected)) return;
            if (isPath && pathEditor.SelectedEntry is { } entry) entry.Value = selected;
            else value.Text = selected;
        };
        var cancel = new Button { Content = labels("common.cancel"), MinWidth = 88 };
        cancel.Click += (_, _) => cancelEdit();
        var save = new Button { Content = labels("common.save"), MinWidth = 88 };
        save.Click += (_, _) =>
        {
            var variableName = name.Text?.Trim() ?? "";
            var variableValue = isPath ? pathEditor.JoinedValue : value.Text ?? "";
            if (!EnvironmentValidation.IsValidName(variableName, windows)
                || variableValue.Length > EnvironmentValidation.MaximumValueLength)
                return;
            complete(new EnvironmentMutation(variableName, EnvironmentMutationKind.Set, variableValue,
                existing?.ValueKind ?? EnvironmentValueKind.String));
        };
        var content = new Grid
        {
            Margin = new Thickness(20, 0, 20, 12), RowSpacing = 10,
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
        };
        content.Children.Add(new TextBlock { Text = labels(isPath ? "settings.environment.path_entries" : "settings.environment.value") });
        if (isPath)
        {
            var pathEditorLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            pathEditorLayout.Children.Add(pathList);
            Grid.SetColumn(pathButtons, 1);
            pathEditorLayout.Children.Add(pathButtons);
            pathEditorLayout.SizeChanged += (_, args) =>
            {
                var narrow = args.NewSize.Width < 420;
                pathEditorLayout.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
                pathEditorLayout.RowDefinitions = new RowDefinitions(narrow ? "*,Auto" : "*");
                Grid.SetColumn(pathButtons, narrow ? 0 : 1);
                Grid.SetRow(pathButtons, narrow ? 1 : 0);
                pathButtons.Orientation = narrow ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
                pathButtons.Spacing = narrow ? 2 : 7;
                foreach (var button in pathButtons.Children.OfType<Button>())
                    button.MinWidth = narrow ? 0 : 82;
            };
            Grid.SetRow(pathEditorLayout, 1);
            content.Children.Add(pathEditorLayout);
            Grid.SetRow(pathEntry, 2);
            content.Children.Add(pathEntry);
        }
        else { Grid.SetRow(value, 1); content.Children.Add(value); }
        Grid.SetRow(browse, 3);
        content.Children.Add(browse);
        var footer = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 12), Spacing = 8, Children = { cancel, save },
        };
        var nameHeader = new StackPanel
        {
            Margin = new Thickness(20, 20, 20, 12), Spacing = 10,
            Children =
            {
                new TextBlock { Text = labels("settings.environment.name") },
                name,
            },
        };
        var editorLayout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        editorLayout.Children.Add(nameHeader);
        Grid.SetRow(content, 1);
        editorLayout.Children.Add(content);
        Grid.SetRow(footer, 2);
        editorLayout.Children.Add(footer);
        return editorLayout;
    }
}

/// <summary>Owns the PATH edit dialog's list and current item so both controls stay in sync.</summary>
internal sealed partial class EnvironmentPathEditor : ObservableObject
{
    public ObservableCollection<EnvironmentPathEntry> Entries { get; }

    [ObservableProperty] private EnvironmentPathEntry? _selectedEntry;

    public bool HasSelection => SelectedEntry is not null;
    public string JoinedValue => string.Join(_separator, Entries.Select(entry => entry.Value));
    private readonly string _separator;

    public EnvironmentPathEditor(string value, bool isPath, string separator)
    {
        _separator = separator;
        Entries = new(isPath
            ? value.Split(separator, StringSplitOptions.None).Select(entry => new EnvironmentPathEntry(entry))
            : Array.Empty<EnvironmentPathEntry>());
    }

    partial void OnSelectedEntryChanged(EnvironmentPathEntry? value) => OnPropertyChanged(nameof(HasSelection));

    public void Add()
    {
        var entry = new EnvironmentPathEntry("");
        Entries.Add(entry);
        SelectedEntry = entry;
    }

    public void RemoveSelected()
    {
        if (SelectedEntry is not { } entry) return;
        var index = Entries.IndexOf(entry);
        if (index < 0) return;
        Entries.RemoveAt(index);
        SelectedEntry = Entries.Count == 0 ? null : Entries[Math.Min(index, Entries.Count - 1)];
    }

    public void MoveSelected(int offset)
    {
        if (SelectedEntry is not { } entry) return;
        var index = Entries.IndexOf(entry);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= Entries.Count) return;
        Entries.Move(index, destination);
        // ListBox may clear selection while processing the collection move.
        SelectedEntry = entry;
    }
}

internal sealed partial class EnvironmentPathEntry(string value) : ObservableObject
{
    [ObservableProperty] private string _value = value;
}
