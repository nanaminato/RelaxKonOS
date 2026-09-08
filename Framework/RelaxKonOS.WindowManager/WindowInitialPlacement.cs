namespace RelaxKonOS.WindowManager;

/// <summary>Determines whether a window uses an exact position or the desktop's default placement.</summary>
public enum WindowInitialPlacement
{
    /// <summary>Use the position supplied in <see cref="WindowCreateOptions.Bounds"/>.</summary>
    Explicit,

    /// <summary>Center the window in the work area and cascade successive windows.</summary>
    CenteredCascade,
}
