using RelaxKonOS.Client.Services.Developer;

var previous = Environment.GetEnvironmentVariable("RELAXKONOS_DATA_HOME");
var root = Path.Combine(AppContext.BaseDirectory, "profile-checks", Guid.NewGuid().ToString("N"));
try
{
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", null);
    var defaultRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    Check(DeveloperProfile.DataDirectory == Expected(defaultRoot), "Default data location");
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", root);
    Check(DeveloperProfile.DataDirectory == Expected(root), "Explicit data location");
    using (DeveloperProfile.Acquire())
    {
        Check(Directory.Exists(Expected(root)), "Data directory created");
        Check(!Directory.EnumerateFiles(Expected(root), ".write-test-*").Any(), "Write probe cleaned up");
        if (DeveloperProfile.IsDebug)
        {
            try
            {
                using var duplicate = DeveloperProfile.Acquire();
                throw new Exception("Duplicate profile unexpectedly opened");
            }
            catch (InvalidOperationException exception)
            {
                Check(exception.Message.Contains("RELAXKONOS_DEV_PROFILE"), "Lock conflict guidance");
            }
        }
    }
    using (DeveloperProfile.Acquire()) { }
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", "relative-data");
    Check(DeveloperProfile.DataDirectory == Expected(Path.Combine(AppContext.BaseDirectory, "relative-data")), "Stable relative path");
    var blocked = Path.Combine(root, "blocked");
    File.WriteAllText(blocked, "A file cannot be used as a directory");
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", blocked);
    try
    {
        using var profile = DeveloperProfile.Acquire();
        throw new Exception("Unavailable directory unexpectedly opened");
    }
    catch (InvalidOperationException exception)
    {
        Check(exception.Message.Contains("RELAXKONOS_DATA_HOME") && exception.Message.Contains(blocked), "Unavailable path guidance");
    }
    Console.WriteLine($"Developer profile checks passed ({(DeveloperProfile.IsDebug ? "Debug" : "Release")}).");
}
finally
{
    Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", previous);
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static string Expected(string root) => DeveloperProfile.IsDebug
    ? Path.Combine(root, "RelaxKonOS", "Debug", DeveloperProfile.Name)
    : Path.Combine(root, "RelaxKonOS");
static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
