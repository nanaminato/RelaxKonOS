using Avalonia.Controls;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.Git;

/// <summary>Presents a Git dialog over its active workspace or a nested dialog.</summary>
public interface IGitDialogPresenter
{
    Task<TResult?> ShowDialogAsync<TResult>(ManagedWindow owner, string title,
        Func<ModalDialog<TResult>, Control> content, Size preferredSize);
}
