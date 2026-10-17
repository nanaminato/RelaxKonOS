using System.Text.Json;
using RelaxKonOS.PrivilegedHelper;

internal static class NativeProcessChecks
{
    internal static void RunFixture(string mode)
    {
        switch (mode)
        {
            case "environment":
                Console.Write(JsonSerializer.Serialize(new
                {
                    Secret = Environment.GetEnvironmentVariable("RELAXKONOS_NATIVE_TEST_SECRET"),
                    Proxy = Environment.GetEnvironmentVariable("HTTP_PROXY"),
                    Path = Environment.GetEnvironmentVariable("PATH")
                }));
                break;
            case "oversize": Console.Write(new string('x', 9000)); break;
            case "failure": Console.Error.Write("secret-native-diagnostic"); Environment.ExitCode = 17; break;
        }
    }

    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var process = Environment.ProcessPath!;
        string[] Arguments(string mode) => string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? [typeof(NativeProcessChecks).Assembly.Location, "native-probe-fixture", mode] : ["native-probe-fixture", mode];
        var oldSecret = Environment.GetEnvironmentVariable("RELAXKONOS_NATIVE_TEST_SECRET");
        var oldProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("RELAXKONOS_NATIVE_TEST_SECRET", "must-not-reach-child");
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://credential:secret@127.0.0.1:1234");
            Environment.SetEnvironmentVariable("PATH", "untrusted-parent-search-path");
            var environment = await SingBoxOperations.RunAsync(process, Arguments("environment"));
            using var json = JsonDocument.Parse(environment);
            Check(json.RootElement.GetProperty("Secret").ValueKind == JsonValueKind.Null
                && json.RootElement.GetProperty("Proxy").ValueKind == JsonValueKind.Null,
                "Actual native child process does not inherit parent secrets or download proxy");
            Check(json.RootElement.GetProperty("Path").GetString() != Environment.GetEnvironmentVariable("PATH"),
                "Native child uses fixed trusted PATH");
            try { await SingBoxOperations.RunAsync(process, Arguments("oversize")); throw new Exception("Oversized output was silently truncated"); }
            catch (IOException ex) { Check(ex.Message == "sing-box output exceeds limit.", "Actual oversized output is rejected"); }
            try { await SingBoxOperations.RunAsync(process, Arguments("failure")); throw new Exception("Native failure accepted"); }
            catch (InvalidOperationException ex)
            { Check(!ex.ToString().Contains("secret-native-diagnostic", StringComparison.Ordinal), "Actual native diagnostics do not cross privilege error boundary"); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("RELAXKONOS_NATIVE_TEST_SECRET", oldSecret);
            Environment.SetEnvironmentVariable("HTTP_PROXY", oldProxy);
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
        return count;
    }
}
