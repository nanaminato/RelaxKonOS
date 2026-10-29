using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.FileServices;

internal static class InstallationRoutingChecks
{
    public static Task RunAsync(FileServicesViewModel host)
    {
        // SMB has no installer of its own any more; the settings area only observes and navigates.
        if (typeof(FileServicesViewModel).GetProperty("InstallCommand") is not null)
            throw new Exception("SMB must not expose its former application installer.");
        if (typeof(FileServicesViewModel).GetProperty("ManageComponentCommand") is not null)
            throw new Exception("SMB must not keep a duplicate component navigation outside its settings area.");
        var navigations = new List<string>();
        var dependency = new ComponentDependencySettingsViewModel("relaxkonos.file-services", null, null, null!,
            componentId => navigations.Add(componentId));
        if (dependency.Components.Count != 1 || dependency.Components[0].ComponentId != "samba")
            throw new Exception("SMB dependency settings must observe exactly the catalog's samba component.");
        dependency.Components[0].ManageCommand.Execute(null);
        if (navigations.Count != 1 || navigations[0] != "samba")
            throw new Exception("SMB dependency settings must navigate to the samba component.");
        dependency.Dispose();
        Console.WriteLine("PASS: SMB dependency settings replace the application installer.");
        return Task.CompletedTask;
    }
}
