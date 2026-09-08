namespace RelaxKonOS.Core.Applications;

/// <summary>
/// Controls how the local RelaxKonOS runtime treats repeated activations of an application.
/// This is a desktop-window policy, not a process-isolation boundary.
/// </summary>
public enum ApplicationInstancePolicy
{
    /// <summary>Every activation may create another primary application window.</summary>
    MultiWindow,

    /// <summary>Reuse the application's existing primary window and deliver the activation to it.</summary>
    SingleWindow,

    /// <summary>
    /// Reserved for applications which expose a host-normalized activation key (for example a
    /// workspace root). It currently behaves as <see cref="MultiWindow"/> until such a key is
    /// supplied by a route handler.
    /// </summary>
    SingleWindowPerActivationKey,
}
