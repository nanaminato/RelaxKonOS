internal static class TestAssert
{
    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void True(bool condition, string message) => Assert(condition, message);

    public static void Equal<T>(T expected, T actual, string message)
        => Assert(System.Collections.Generic.EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message} (expected={expected}, actual={actual})");
}
