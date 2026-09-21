using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using RelaxKonOS.Client.Mobile;

namespace RelaxKonOS.Client.Android;

/// <summary>Android-only host. Mobile pages and all business rules remain in RelaxKonOS.Client.Mobile/Foundation.</summary>
[Activity(
    Label = "RelaxKonOS",
    Theme = "@style/RelaxKonOSMobileTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidLifecycleService.NotifyCreated();
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidLifecycleService.NotifyForeground();
    }

    protected override void OnStop()
    {
        AndroidLifecycleService.NotifyBackground();
        base.OnStop();
    }
}
