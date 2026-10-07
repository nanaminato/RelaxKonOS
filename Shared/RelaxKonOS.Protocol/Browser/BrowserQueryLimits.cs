namespace RelaxKonOS.Protocol.Browser;

/// <summary>Bounded browser collection queries, shared by all callers.</summary>
public static class BrowserQueryLimits
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 500;
    public static int PageSize(int value) => Math.Clamp(value, 1, MaximumPageSize);
    public static int Offset(int value) => Math.Max(0, value);
}
