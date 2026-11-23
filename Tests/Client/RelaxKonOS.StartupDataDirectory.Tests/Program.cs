using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Views;

AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var root = Path.Combine(AppContext.BaseDirectory, "startup-checks", Guid.NewGuid().ToString("N"));
var locationFile = Path.Combine(AppContext.BaseDirectory, "data-location.txt");
var previous = Environment.GetEnvironmentVariable("RELAXKONOS_DATA_HOME");
Directory.CreateDirectory(root);
try
{
    var blocked = Path.Combine(root, "blocked");
    File.WriteAllText(blocked, "not a directory");
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", blocked);
    var window = (StartupDataDirectoryWindow)Activator.CreateInstance(typeof(StartupDataDirectoryWindow),
        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { new IOException("Initial failure") }, null)!;
    window.Show();
    var path = window.GetVisualDescendants().OfType<TextBox>().Single();
    var apply = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "使用此目录"));
    var completion = (TaskCompletionSource<(bool Ready, IDisposable? Profile)>)typeof(StartupDataDirectoryWindow)
        .GetField("_completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(!completion.Task.IsCompleted && window.IsVisible, "Invalid path stays open for retry");
    path.Text = Path.Combine(root, "valid-data");
    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(completion.Task.IsCompletedSuccessfully, "Valid path continues startup");
    var ready = completion.Task.Result;
    Check(ready.Ready, "Recovery succeeds");
    Check(File.ReadAllText(locationFile) == path.Text, "Selected parent is remembered");
    ready.Profile?.Dispose();
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", null);
    var remembered = StartupDataDirectoryWindow.PrepareAsync();
    Check(remembered.IsCompletedSuccessfully && remembered.Result.Ready, "Restart uses remembered selection");
    remembered.Result.Profile?.Dispose();

    // A read-only location preference cannot prevent using an otherwise writable data directory.
    File.SetAttributes(locationFile, FileAttributes.ReadOnly);
    var noSave = (StartupDataDirectoryWindow)Activator.CreateInstance(typeof(StartupDataDirectoryWindow),
        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { new IOException("Initial failure") }, null)!;
    noSave.Show();
    var noSaveApply = noSave.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "使用此目录"));
    noSaveApply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(noSave.IsVisible && Equals(noSaveApply.Content, "仅本次使用并继续"), "Preference failure offers session-only startup");
    noSave.Close();
    using (DeveloperProfile.Acquire()) { }
    File.SetAttributes(locationFile, FileAttributes.Normal);

    var canceled = (StartupDataDirectoryWindow)Activator.CreateInstance(typeof(StartupDataDirectoryWindow),
        BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { new IOException("Initial failure") }, null)!;
    canceled.Show();
    var canceledCompletion = (TaskCompletionSource<(bool Ready, IDisposable? Profile)>)typeof(StartupDataDirectoryWindow)
        .GetField("_completion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canceled)!;
    canceled.Close();
    Check(canceledCompletion.Task.IsCompletedSuccessfully && !canceledCompletion.Task.Result.Ready, "Cancel ends startup cleanly");
    Dispatcher.UIThread.RunJobs();
    Console.WriteLine("Startup data directory recovery checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", previous);
    if (File.Exists(locationFile)) { File.SetAttributes(locationFile, FileAttributes.Normal); File.Delete(locationFile); }
    Directory.Delete(root, true);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
