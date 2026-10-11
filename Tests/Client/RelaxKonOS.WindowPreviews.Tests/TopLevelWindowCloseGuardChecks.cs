using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Services.Dialogs;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

/// <summary>
/// Verifies the top-level window close guard added for the desktop UI review: a user close gesture
/// (title-bar button, Alt+F4, taskbar, window overview) consults
/// <see cref="ManagedWindow.CanCloseAsync"/>, while teardown bypasses it. Uses the real
/// <see cref="WindowManager"/> and the real discard-confirmation view.
/// </summary>
internal static class TopLevelWindowCloseGuardChecks
{
    public static void Run()
    {
        // A window nothing guards keeps closing immediately; the shared close path must not
        // change behavior for the applications that were already correct.
        {
            var manager = NewManager(out var window);
            manager.Close(window);
            Check(manager.Windows.Count == 0, "A window without a close guard closes immediately.");
        }

        // Busy work blocks the close and shows no prompt.
        // Pending state that the user resolves by confirming closes without any prompt.
        {
            var manager = NewManager(out var window);
            var busy = true;
            var changes = true;
            DraftWindowCloseGuard.Attach(manager, window, () => busy, () => changes);

            manager.Close(window);
            Check(manager.Windows.Count == 1 && !AnyConfirmation(manager),
                "Busy work blocks the close without prompting.");

            busy = false;
            changes = false;
            manager.Close(window);
            Check(manager.Windows.Count == 0, "A window without pending state closes without prompting.");
        }

        // Pending state opens exactly one confirmation; declining keeps the window, accepting
        // discards and closes it.
        {
            var manager = NewManager(out var window);
            DraftWindowCloseGuard.Attach(manager, window, () => false, () => true);

            manager.Close(window);
            Check(manager.Windows.Count == 2 && AnyConfirmation(manager),
                "Pending state opens one discard confirmation.");

            Confirmation(manager).NoCommand.Execute(null);
            PumpUntil(() => manager.Windows.Count == 1);
            Check(manager.Windows.Contains(window), "Declining the confirmation keeps the window open.");

            manager.Close(window);
            PumpUntil(() => AnyConfirmation(manager));
            Check(manager.Windows.Count == 2, "A later close gesture asks again.");
            Confirmation(manager).YesCommand.Execute(null);
            PumpUntil(() => manager.Windows.Count == 0);
            Check(!manager.Windows.Contains(window), "Accepting the confirmation discards and closes the window.");
        }

        // A second gesture while the guard is still running must not stack another confirmation.
        {
            var manager = NewManager(out var window);
            var checks = 0;
            var gate = new TaskCompletionSource<bool>();
            window.CanCloseAsync = () => { checks++; return gate.Task; };

            manager.Close(window);
            manager.Close(window);
            manager.Close(window);
            Check(checks == 1 && manager.Windows.Count == 1,
                "Repeat close gestures run the guard once.");

            gate.SetResult(false);
            Dispatcher.UIThread.RunJobs();
            Check(manager.Windows.Contains(window), "A vetoed guard keeps the window open.");
        }

        // The title-bar close button is the same request path as the other gestures.
        {
            var manager = NewManager(out var window);
            var checks = 0;
            window.CanCloseAsync = () => { checks++; return Task.FromResult(false); };

            window.CloseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Check(checks == 1 && manager.Windows.Contains(window),
                "The title-bar close button honours the guard.");
        }

        // Teardown must never block on a prompt: session end, uninstall and automation force close.
        {
            var manager = NewManager(out var window);
            var checks = 0;
            window.CanCloseAsync = () => { checks++; return Task.FromResult(false); };

            manager.ForceClose(window);
            Check(checks == 0 && manager.Windows.Count == 0,
                "ForceClose bypasses the guard for teardown.");
        }

        // A guard that fails must not silently discard the window's state.
        {
            var manager = NewManager(out var window);
            window.CanCloseAsync = () => throw new InvalidOperationException("guard failed");

            manager.Close(window);
            Check(manager.Windows.Contains(window), "A failing guard keeps the window instead of discarding it.");
            manager.ForceClose(window);
        }

        Console.WriteLine("Top-level window close guard checks passed.");
    }

    private static WindowManagerService NewManager(out ManagedWindow window)
    {
        var manager = new WindowManagerService();
        manager.Attach(new Canvas { Width = 1000, Height = 700 });
        window = manager.Create(new WindowCreateOptions(new AppId("test.close-guard"), "Guarded window",
            new TextBlock { Text = "content" }, new Rect(0, 0, 700, 500)));
        return manager;
    }

    private static bool AnyConfirmation(WindowManagerService manager)
        => manager.Windows.Any(candidate => candidate.View.Content is ConfirmDialogView);

    private static ConfirmDialogViewModel Confirmation(WindowManagerService manager)
        => (ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!;

    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Window close guard did not settle.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
