using RelaxKonOS.Protocol.Settings;

internal static class HostIdentityChecks
{
    public static void Run()
    {
        // Shared syntax rules. The server re-validates and the local Helper validates a third time.
        Accept("relaxkon-host", 15);
        Accept("a", 15);
        Accept("a1-b2", 63);
        Reject("", 15);
        Reject(" ", 15);
        Reject(" host", 15);
        Reject("host ", 15);
        Reject("-host", 15);
        Reject("host-", 15);
        Reject("_host", 15);
        Reject("host_name", 15);
        Reject("host.name", 15);
        Reject("host.example.com", 63);
        Reject("host/name", 63);
        Reject("ホスト", 63);
        Reject("12345", 15);
        Reject("host\u0000", 15);
        Reject("host\nname", 15);
        Reject("relaxkon-host-01", 15);   // 16 characters, over the Windows NetBIOS limit
        Accept("relaxkon-host-01", 63);   // the same name is inside the Linux limit

        // The remote provider reports the limit, so a client never guesses the target platform.
        if (HostIdentityValidation.MaximumLength(true) != 15 || HostIdentityValidation.MaximumLength(false) != 63)
            throw new Exception("Platform host name limits changed without a migration plan.");
        Console.WriteLine("Host identity checks passed: label syntax, platform-bounded length, remote-reported maximum.");
    }

    private static void Accept(string name, int maximumLength)
    {
        if (HostIdentityValidation.Validate(new HostnameChange(name), maximumLength) is not null)
            throw new Exception($"A valid host name was rejected at limit {maximumLength}.");
    }

    private static void Reject(string name, int maximumLength)
    {
        if (HostIdentityValidation.Validate(new HostnameChange(name), maximumLength) != "settings.identity.invalid_name")
            throw new Exception($"An invalid host name of length {name.Length} was accepted at limit {maximumLength}.");
    }
}
