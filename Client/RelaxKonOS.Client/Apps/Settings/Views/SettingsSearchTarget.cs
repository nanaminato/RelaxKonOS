using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace RelaxKonOS.Client.Apps.Settings.Views;

/// <summary>Only view-owned setting IDs can be revealed; remote metadata cannot select arbitrary controls.</summary>
public sealed class SettingsSearchTarget : AvaloniaObject
{
    public static readonly AttachedProperty<string?> IdProperty =
        AvaloniaProperty.RegisterAttached<SettingsSearchTarget, Control, string?>("Id");
    public static string? GetId(Control control) => control.GetValue(IdProperty);
    public static void SetId(Control control, string? value) => control.SetValue(IdProperty, value);

    public static Control? Find(Control root, string id) => root.GetVisualDescendants().OfType<Control>()
        .Prepend(root).FirstOrDefault(control => GetId(control) == id && control.IsEffectivelyVisible);

    public static SettingsSearchHighlight HighlightAndFocus(Control target, string? title = null)
    {
        var highlight = target as Border ?? target.FindAncestorOfType<Border>() ?? target;
        highlight.Classes.Add("settings-search-target");
        var temporaryFocus = false;
        string? originalName = null;
        var temporaryName = false;
        var focus = target.Focusable && target.IsEffectivelyEnabled ? target
            : target.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
                control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled);
        if (focus is not null) focus.Focus(NavigationMethod.Tab);
        else if (highlight.IsEffectivelyEnabled)
        {
            // A read-only or disabled setting still has an accessible section to land on.
            if (!string.IsNullOrWhiteSpace(title))
            {
                originalName = AutomationProperties.GetName(highlight);
                temporaryName = true;
                AutomationProperties.SetName(highlight, title);
            }
            temporaryFocus = !highlight.Focusable;
            highlight.Focusable = true;
            highlight.Focus(NavigationMethod.Tab);
        }
        target.BringIntoView();
        return new SettingsSearchHighlight(highlight, temporaryFocus, temporaryName, originalName);
    }
}

/// <summary>Restore temporary focusability and decoration when navigation or the timer clears a reveal.</summary>
public sealed class SettingsSearchHighlight(Control target, bool temporaryFocus, bool temporaryName, string? originalName) : IDisposable
{
    public Control Target { get; } = target;
    public void Dispose()
    {
        Target.Classes.Remove("settings-search-target");
        if (temporaryFocus) Target.Focusable = false;
        if (temporaryName) AutomationProperties.SetName(Target, originalName);
    }
}
