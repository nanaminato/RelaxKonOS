using System.Windows.Input;

namespace Example.Windows11DesktopShell.Commands;

public sealed class DelegateCommand : ICommand
{
    private readonly Action? _action;
    private readonly Func<Task>? _asyncAction;

    public DelegateCommand(Action action) => _action = action;
    public DelegateCommand(Func<Task> action) => _asyncAction = action;

    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter)
    {
        if (_action is not null) _action();
        else if (_asyncAction is not null) await _asyncAction();
    }
}
