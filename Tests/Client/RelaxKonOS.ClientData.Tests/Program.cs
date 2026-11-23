using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.ServerCenter;

const string serviceId = "client-storage-acceptance";
if (args.Length == 2 && args[0] is "--write" or "--read")
{
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", Path.GetFullPath(args[1]));
    using var profile = DeveloperProfile.Acquire();
    var root = ClientDataPaths.Root;
    var expectedRoot = DeveloperProfile.IsDebug
        ? Path.Combine(Path.GetFullPath(args[1]), "RelaxKonOS", "Debug", DeveloperProfile.Name)
        : Path.Combine(Path.GetFullPath(args[1]), "RelaxKonOS");
    Check(root == expectedRoot, "Selected data root");
    var keys = new OwnerDeviceKeyStore();
    var evidence = Path.Combine(root, "key-public.spki");
    if (args[0] == "--write")
    {
        var material = await keys.CreateAsync(serviceId, "Storage acceptance", "windows", "test", null);
        var enrolled = material with { DeviceId = Guid.NewGuid() };
        await keys.SaveAsync(enrolled, null);
        await File.WriteAllTextAsync(evidence, enrolled.PublicKeySpki);
        Check(File.Exists(Path.Combine(root, "owner-device-keys.bin")), "Windows key file is inside selected root");
        var encrypted = await File.ReadAllBytesAsync(Path.Combine(root, "owner-device-keys.bin"));
        Check(encrypted.AsSpan().IndexOf(enrolled.PrivateKeyPkcs8) < 0, "Private key is not stored as plaintext");
        new LocalLanguageStore().Save("ja-JP");
        await new OwnerDevicePairingEndpointStore().SaveAsync(serviceId, "https://storage-acceptance.example");
    }
    else
    {
        var material = await keys.LoadAsync(serviceId, null) ?? throw new Exception("Key missing after process restart");
        Check(material.DeviceId != Guid.Empty, "Updated enrollment survives restart");
        Check(material.PublicKeySpki == await File.ReadAllTextAsync(evidence), "Same public identity after restart");
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(material.PrivateKeyPkcs8, out _);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(await File.ReadAllTextAsync(evidence)), out _);
        var challenge = RandomNumberGenerator.GetBytes(32);
        Check(verifier.VerifyData(challenge, signer.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256),
            "Persisted private key signs successfully after restart");
        Check(new LocalLanguageStore().Load() == "ja-JP", "Preference survives restart");
        Check(await new OwnerDevicePairingEndpointStore().GetAsync(serviceId) == "https://storage-acceptance.example",
            "Pairing endpoint survives restart");
    }
    object[] stores =
    [
        keys, new OwnerDevicePairingEndpointStore(), new RememberedSessionStore(),
        new ServerCertificateTrust(), new HostTargetStore(), new SshCredentialStore(),
        new SshHostKeyTrustStore(), new ServerCenterOperationJournal(), new LoginTunnelStore(),
        new LocalLanguageStore(), new DesktopDevicePreferences(), new DesktopWelcomePreferenceStore(),
        new LoginNotificationPreferenceStore(), new UsageMemoryStore()
    ];
    var checkedPaths = 0;
    foreach (var store in stores)
        foreach (var field in store.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            if (field.FieldType == typeof(string) && field.GetValue(store) is string path && Path.IsPathFullyQualified(path))
            {
                Check(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    $"{store.GetType().Name}.{field.Name} follows selected root");
                checkedPaths++;
            }
    Check(checkedPaths >= 16, "Persistent store path coverage");
    var configureBrowser = typeof(RelaxKonOS.Client.Apps.Browser.Views.BrowserMainView)
        .GetMethod("ConfigureWebViewEnvironment", BindingFlags.Static | BindingFlags.NonPublic)!;
    var windowsBrowser = BrowserArgs<Avalonia.Platform.WindowsWebView2EnvironmentRequestedEventArgs>();
    configureBrowser.Invoke(null, new object?[] { null, windowsBrowser });
    Check(windowsBrowser.UserDataFolder == Path.Combine(root, "browser"), "WebView2 data follows selected root");
    var gtkBrowser = BrowserArgs<Avalonia.Platform.GtkWebViewEnvironmentRequestedEventArgs>();
    configureBrowser.Invoke(null, new object?[] { null, gtkBrowser });
    Check(gtkBrowser.BaseDataDirectory == Path.Combine(root, "browser")
        && gtkBrowser.BaseCacheDirectory == Path.Combine(root, "browser", "cache"), "GTK browser data and cache follow root");
    var wpeBrowser = BrowserArgs<Avalonia.Platform.LinuxWpeWebViewEnvironmentRequestedEventArgs>();
    configureBrowser.Invoke(null, new object?[] { null, wpeBrowser });
    Check(wpeBrowser.DataDirectory == Path.Combine(root, "browser")
        && wpeBrowser.CacheDirectory == Path.Combine(root, "browser", "cache"), "WPE browser data and cache follow root");
    var appleBrowser = BrowserArgs<Avalonia.Platform.AppleWKWebViewEnvironmentRequestedEventArgs>();
    configureBrowser.Invoke(null, new object?[] { null, appleBrowser });
    Check(appleBrowser.NonPersistentDataStore, "WKWebView does not persist outside selected root");
    Console.WriteLine($"Device-key {args[0][2..]} and {checkedPaths} store-path checks passed.");
    return;
}

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This acceptance verifies Windows DPAPI persistence.");
var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md"))) repository = repository.Parent;
Check(repository is not null, "Repository found for source audit");
var sourceRoot = Path.Combine(repository!.FullName, "Client", "RelaxKonOS.Client");
foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
    if (relative.StartsWith("obj/") || relative.StartsWith("bin/")) continue;
    var source = File.ReadAllText(file);
    // The deployment broker has separate, short-lived privileged staging ownership.
    if (relative is not ("Services/Developer/DeveloperProfile.cs" or "Services/ServerCenter/LocalWindowsDeploymentBroker.cs"))
        Check(!source.Contains("SpecialFolder.LocalApplicationData") && !source.Contains("SpecialFolder.ApplicationData")
            && !source.Contains("SpecialFolder.CommonApplicationData"), $"No independent OS data directory: {relative}");
}
var directory = Path.Combine(repository.FullName, ".codex-scratch", "client-data-check", "key-restart-" + Guid.NewGuid().ToString("N"));
try
{
    await RunChild("--write");
    await RunChild("--read");
    Console.WriteLine("Unified client storage source audit and process-restart acceptance passed.");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}

async Task RunChild(string mode)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(mode);
    start.ArgumentList.Add(directory);
    using var process = Process.Start(start) ?? throw new Exception("Could not start storage acceptance child");
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { process.Kill(entireProcessTree: true); throw; }
    Check(process.ExitCode == 0, $"Child {mode} failed: {await error}");
    Console.Write(await output);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}

static T BrowserArgs<T>() => (T)Activator.CreateInstance(typeof(T),
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object?[] { null }, null)!;
