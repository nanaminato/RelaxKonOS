using System.Windows.Input;
using RelaxKonOS.Core.Input;

namespace RelaxKonOS.Client.Services;

/// <summary>Executes an application command for an exact, Windows-style key gesture.</summary>
internal static class WindowShortcut
{
    /// <summary>
    /// Runs <paramref name="command"/> only when the gesture matches and the command is available.
    /// Repeated key-down events are ignored so holding a shortcut cannot enqueue the same operation.
    /// </summary>
    public static bool TryExecute(
        RemoteKeyEventArgs e,
        RemoteKey key,
        RemoteKeyModifiers modifiers,
        ICommand command,
        object? parameter = null)
    {
        if (e.Handled || e.IsRepeat || e.Key != key || e.Modifiers != modifiers || !command.CanExecute(parameter))
            return false;

        command.Execute(parameter);
        e.Handled = true;
        return true;
    }
}
