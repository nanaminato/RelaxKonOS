using RelaxKonOS.Runtime;
using System.Globalization;
using System.Reflection;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.HostSettings;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Settings;

internal static class RemoteClockChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization, IServiceProvider services)
    {
        var authenticated = true;
        var failed = false;
        var connection = new HostSettingsConnection("clock-test", Guid.NewGuid(), Guid.NewGuid());
        var observed = new DateTimeOffset(2032, 2, 3, 23, 47, 0, TimeSpan.Zero);
        var session = DispatchProxy.Create<IAuthSession, RemoteClockProxy>();
        ((RemoteClockProxy)session).Handler = (method, _) => method.Name == "get_State"
            ? (authenticated ? AuthSessionState.Authenticated : AuthSessionState.Unauthenticated) : null;
        var time = DispatchProxy.Create<IHostTimeService, RemoteClockProxy>();
        ((RemoteClockProxy)time).Handler = (method, _) => method.Name switch
        {
            "CaptureConnection" => connection,
            "IsCurrent" => authenticated,
            "ReadAsync" => failed ? Task.FromException<HostTimeSnapshot>(new HttpRequestException("offline"))
                : Task.FromResult(new HostTimeSnapshot(new("Pacific/Kiritimati", [], "test", observed, "test"),
                    new("host/time", SettingsScope.HostMachine), new(SettingsCapabilityState.Available))),
            _ => throw new InvalidOperationException(method.Name)
        };
        DesktopShellViewModel Create()
        {
            var windows = new RelaxKonOS.WindowManager.WindowManager();
            return new(windows, new ApplicationManager(windows, services), settings, localization,
                session, new SshDesktopSession(null!), () => { },
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, time);
        }
        var expected = TimeZoneInfo.ConvertTime(observed, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati"));
        var vm = Create();
        var culture = CultureInfo.GetCultureInfo(settings.Language);
        if (vm.Clock != expected.ToString(settings.TimeFormat == "12h" ? "h:mm tt" : "HH:mm", culture)
            || vm.DateText != expected.ToString(string.IsNullOrWhiteSpace(settings.DateFormat) ? "M/d ddd" : settings.DateFormat, culture))
            throw new InvalidOperationException("Taskbar must use server time and zone, including the next-day date.");
        failed = true;
        if (Create().Clock != "—:—") throw new InvalidOperationException("Failed synchronization must not show local time.");
        authenticated = false;
        if (Create().Clock != "—:—") throw new InvalidOperationException("Disconnected clock must not show local time.");
        Console.WriteLine("PASS: Taskbar uses remote time/time zone and hides local time on failure or disconnect.");
    }
}

public class RemoteClockProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
}
