using RelaxKonOS.Client.Apps.FileServices;

internal static class InstallationRoutingChecks
{
    public static Task RunAsync(FileServicesViewModel host)
    {
        var navigations = 0;
        host.OpenComponentCenter = () => navigations++;
        host.ManageComponentCommand.Execute(null);
        if (navigations != 1) throw new Exception("SMB installation must navigate to Component Center.");
        if (typeof(FileServicesViewModel).GetProperty("InstallCommand") is not null)
            throw new Exception("SMB must not expose its former application installer.");
        Console.WriteLine("PASS: SMB dependency navigation replaces the application installer.");
        return Task.CompletedTask;
    }
}
