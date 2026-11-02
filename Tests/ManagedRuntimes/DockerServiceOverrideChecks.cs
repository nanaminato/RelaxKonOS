using RelaxKonOS.ManagedRuntimes.ComponentPackages;

/// <summary>
/// A configured Docker proxy used to make the installed engine permanently unverifiable: the drop-in
/// lives in the unit's drop-in directory, and package ownership refused every drop-in as an
/// administrator override. Component Center then lost "纳入管理", update and uninstall for a package it
/// had installed itself. These checks pin which drop-ins leave the unit verifiable and which still
/// change what systemd executes.
/// </summary>
internal static class DockerServiceOverrideChecks
{
    public static int Run()
    {
        var count = 0;
        var content = ManagedServiceOverride.SerializeDockerProxy("http://127.0.0.1:3128", "http://127.0.0.1:3128", "localhost,127.0.0.1");
        Check(ManagedServiceOverride.IsProxyOnlyUnitOverride(content),
            "the docker proxy drop-in written by the Helper keeps the packaged unit verifiable.");
        count++;

        var escaped = ManagedServiceOverride.SerializeDockerProxy("http://host:100%25", "http://host:100%25", "");
        Check(escaped.Contains("HTTP_PROXY=http://host:100%%25\"", StringComparison.Ordinal)
              && ManagedServiceOverride.IsProxyOnlyUnitOverride(escaped),
            "a literal percent is escaped for systemd and the escaped value still verifies.");
        count++;

        // The documented way to proxy Docker by hand, without this product's marker line: the daemon
        // runs the same packaged binary with the same start command, so ownership is still provable.
        const string handWritten =
            "[Service]\nEnvironment=\"HTTP_PROXY=http://192.168.1.2:7890\"\nEnvironment=\"HTTPS_PROXY=http://192.168.1.2:7890\"\nEnvironment=\"NO_PROXY=localhost,127.0.0.1\"\n";
        Check(ManagedServiceOverride.IsProxyOnlyUnitOverride(handWritten),
            "a hand-written proxy drop-in is accepted because it cannot change the executed unit.");
        count++;
        Check(ManagedServiceOverride.IsProxyOnlyUnitOverride(
                "# tuned by the administrator\n[Service]\nEnvironment=HTTP_PROXY=http://192.168.1.2:7890\n"),
            "a bare unquoted proxy assignment with comments is accepted.");
        count++;
        Check(ManagedServiceOverride.IsProxyOnlyUnitOverride(""),
            "an empty drop-in loads nothing and cannot carry an override.");
        count++;

        Check(ManagedServiceOverride.IsProxyOnlyDropInSet(["http-proxy.conf"], _ => content)
              && ManagedServiceOverride.IsProxyOnlyDropInSet(["10-proxy.conf", "20-proxy.conf"], _ => handWritten)
              && ManagedServiceOverride.IsProxyOnlyDropInSet([], _ => null),
            "a drop-in directory that only holds proxy configuration is accepted, including an empty one.");
        count++;

        // Everything below can change what the unit runs; the identity check has to keep refusing it.
        Check(!ManagedServiceOverride.IsProxyOnlyUnitOverride(content.Replace("[Service]\n", "[Service]\nExecStart=/usr/bin/elsewhere\n", StringComparison.Ordinal)),
            "an added start command is refused even when the proxy values are intact.");
        count++;
        Check(!ManagedServiceOverride.IsProxyOnlyUnitOverride(content.Replace("[Service]\n", "[Unit]\n", StringComparison.Ordinal)),
            "another section is refused instead of being read as proxy configuration.");
        count++;
        Check(!ManagedServiceOverride.IsProxyOnlyUnitOverride(handWritten.Replace("NO_PROXY=", "LD_PRELOAD=", StringComparison.Ordinal)),
            "a non-proxy environment variable is refused, because it can change what is loaded.");
        count++;
        Check(!ManagedServiceOverride.IsProxyOnlyUnitOverride(handWritten.Replace("Environment=", "EnvironmentFile=", StringComparison.Ordinal)),
            "an environment file is refused: its contents are outside what this check can see.");
        count++;
        Check(!ManagedServiceOverride.IsProxyOnlyUnitOverride(handWritten.Replace("[Service]\n", "", StringComparison.Ordinal)),
            "an assignment outside any section is not a unit override this check accepts.");
        count++;
        Check(!ManagedServiceOverride.IsProxyOnlyDropInSet(["override.conf.bak"], _ => handWritten)
              && !ManagedServiceOverride.IsProxyOnlyDropInSet(["http-proxy.conf"], _ => null),
            "an unreadable file or a non-unit file keeps the administrator-override refusal.");
        count++;
        Check(ManagedServiceOverride.DockerDropInDirectory.EndsWith("/docker.service.d", StringComparison.Ordinal),
            "the accepted location stays the one fixed drop-in path.");
        count++;
        return count;
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }
}
