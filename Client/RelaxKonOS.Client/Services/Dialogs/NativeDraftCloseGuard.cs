using Avalonia.Controls;
using Avalonia.Threading;

namespace RelaxKonOS.Client.Services.Dialogs;

/// <summary>Protects native editors while preserving their platform close gestures.</summary>
public sealed class NativeDraftCloseGuard
{
    private readonly Window _owner;
    private readonly Func<bool> _isBusy;
    private readonly Func<bool> _hasChanges;
    private readonly Func<Task<bool>> _confirmDiscard;
    private readonly Action _clearSecrets;
    private bool _checking;
    private bool _approvedClose;

    public NativeDraftCloseGuard(Window owner, Func<bool> isBusy, Func<bool> hasChanges,
        Func<Task<bool>> confirmDiscard, Action clearSecrets)
    {
        _owner = owner;
        _isBusy = isBusy;
        _hasChanges = hasChanges;
        _confirmDiscard = confirmDiscard;
        _clearSecrets = clearSecrets;
        owner.Closing += OnClosing;
        owner.Closed += (_, _) => { owner.Closing -= OnClosing; _clearSecrets(); };
    }

    public async Task<bool> TryDiscardAsync()
    {
        if (_checking || _isBusy()) return false;
        if (!_hasChanges()) return true;
        _checking = true;
        try { return await _confirmDiscard() && !_isBusy(); }
        finally { _checking = false; }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_approvedClose) return;
        if (_isBusy()) { args.Cancel = true; return; }
        if (!_hasChanges()) return;
        args.Cancel = true;
        _ = CloseAfterConfirmationAsync();
    }

    private async Task CloseAfterConfirmationAsync()
    {
        if (!await TryDiscardAsync()) return;
        // Never call Close recursively while the original Closing event is being dispatched.
        Dispatcher.UIThread.Post(() =>
        {
            if (_isBusy() || !_owner.IsVisible) return;
            _approvedClose = true;
            try { _owner.Close(); }
            finally { _approvedClose = false; }
        });
    }
}
