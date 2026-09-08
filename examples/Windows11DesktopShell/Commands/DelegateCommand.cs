using System.Windows.Input;

namespace RelaxKonOS.Example.Windows11DesktopShell.Commands;

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

public sealed class DelegateCommand<T> : ICommand
{
    private readonly Action<T?>? _action;
    private readonly Func<T?, Task>? _asyncAction;

    public DelegateCommand(Action<T?> action) => _action = action;
    public DelegateCommand(Func<T?, Task> action) => _asyncAction = action;

    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T || parameter is null;

    public async void Execute(object? parameter)
    {
        var value = parameter is T typed ? typed : default;
        if (_action is not null) _action(value);
        else if (_asyncAction is not null) await _asyncAction(value);
    }
}
