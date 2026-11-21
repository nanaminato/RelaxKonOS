namespace RelaxKonOS.App.TaskManager;

public static class TaskManagerLocalization
{
    public static IEnumerable<string> ResourceNames => typeof(TaskManagerLocalization).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("TaskManager.Localization.", StringComparison.Ordinal));

    public static Stream Open(string resourceName) =>
        typeof(TaskManagerLocalization).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Missing Task Manager localization resource '{resourceName}'.");
}
