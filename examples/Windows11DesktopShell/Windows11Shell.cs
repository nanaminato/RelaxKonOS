using Avalonia.Controls;
using Example.Windows11DesktopShell.Services;
using Example.Windows11DesktopShell.ViewModels;
using Example.Windows11DesktopShell.Views;
using RemoteOS.Shell;

namespace Example.Windows11DesktopShell;

/// <summary>Owns only shell lifecycle and host-surface registration; layout lives in AXAML.</summary>
public sealed class Windows11Shell(ShellDescriptor descriptor) : IDesktopShell
{
    private readonly Windows11ShellView _view = new();
    private Windows11ShellViewModel? _viewModel;
    private IShellSurfaceRegistry? _surfaces;

    public ShellDescriptor Descriptor { get; } = descriptor;
    public Control View => _view;

    public Task InitializeAsync(ShellPresentationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _surfaces = context.Surfaces;
        _viewModel = new Windows11ShellViewModel(context, new ShellLocalizer(context.Localization));
        _view.DataContext = _viewModel;
        _view.WindowHostSurface.SizeChanged += OnSurfaceSizeChanged;
        _view.FullScreenHostSurface.SizeChanged += OnSurfaceSizeChanged;
        context.Surfaces.Register(new ShellSurfaces(_view.WindowHostSurface, _view.FullScreenHostSurface,
            _view.ShellOverlaySurface, _view.InputBackdropSurface));
        return Task.CompletedTask;
    }

    public Task ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _viewModel?.Activate();
        ReportWorkArea();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _viewModel?.Deactivate();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _view.WindowHostSurface.SizeChanged -= OnSurfaceSizeChanged;
        _view.FullScreenHostSurface.SizeChanged -= OnSurfaceSizeChanged;
        _viewModel?.Dispose();
        _viewModel = null;
        _view.DataContext = null;
        _surfaces = null;
        return ValueTask.CompletedTask;
    }

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs args) => ReportWorkArea();

    private void ReportWorkArea()
    {
        if (_surfaces is null) return;
        var bounds = _view.WindowHostSurface.Bounds;
        _surfaces.UpdateWorkArea(new RemoteOS.Core.Primitives.Rect(0, 0, bounds.Width, bounds.Height));
    }
}
