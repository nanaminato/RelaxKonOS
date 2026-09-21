namespace RelaxKonOS.Client.Mobile.Navigation;

public enum MobileLayoutState { Compact, Medium, Expanded }

/// <summary>Width is the host-provided usable width after system insets, expressed in device-independent pixels.</summary>
public static class MobileLayoutCalculator
{
    public static MobileLayoutState FromAvailableWidth(double width)
        => width < 600 ? MobileLayoutState.Compact : width < 840 ? MobileLayoutState.Medium : MobileLayoutState.Expanded;
}
