using RelaxKonOS.Client.Mobile.Navigation;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Check(MobileLayoutCalculator.FromAvailableWidth(599.99) == MobileLayoutState.Compact, "599.99dp is Compact");
Check(MobileLayoutCalculator.FromAvailableWidth(600) == MobileLayoutState.Medium, "600dp is Medium");
Check(MobileLayoutCalculator.FromAvailableWidth(839.99) == MobileLayoutState.Medium, "839.99dp is Medium");
Check(MobileLayoutCalculator.FromAvailableWidth(840) == MobileLayoutState.Expanded, "840dp is Expanded");
Console.WriteLine("Mobile layout checks passed.");
