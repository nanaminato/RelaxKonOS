using Android.App;
using Android.Runtime;
using Avalonia.Android;
using RelaxKonOS.Client.Mobile;

namespace RelaxKonOS.Client.Android;

/// <summary>Initializes the Avalonia application for the Android process.</summary>
[Application]
public sealed class AndroidApplication : AvaloniaAndroidApplication<MobileApp>
{
    public AndroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }
}
