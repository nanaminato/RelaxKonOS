using System.Text;
using RelaxKonOS.Protocol.Settings;

internal static class SettingsCliChecks
{
    public static async Task RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "relaxkonos-settings-cli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path,
                """{"changes":[{"name":"EMPTY","operation":"Set","value":""},{"name":"OLD","operation":"Delete"}],"confirmHighImpact":false}""", new UTF8Encoding(true));
            var change = await SettingsCommands.ReadChangesAsync(path);
            if (change.Changes[0].Value != "" || change.Changes[1].Operation != EnvironmentMutationKind.Delete || change.Changes[1].Value is not null)
                throw new Exception("CLI lost empty-value/delete distinction.");
            foreach (var invalid in new[]
            {
                """{"changes":[],"extra":"secret-sentinel"}""",
                """{"changes":[null]}""",
                """{"changes":[{"name":"X","operation":"secret-sentinel"}]}""",
                "null",
                new string(' ', 2 * 1024 * 1024 + 1)
            })
            {
                await File.WriteAllTextAsync(path, invalid);
                try
                {
                    await SettingsCommands.ReadChangesAsync(path);
                    throw new Exception("Invalid CLI change input was accepted.");
                }
                catch (ArgumentException error)
                {
                    if (error.Message.Contains("secret-sentinel")) throw new Exception("CLI leaked invalid input.");
                }
            }
            foreach (var args in new[]
            {
                new[] { "environment", "--scope", "Workspace" },
                new[] { "time", "--reveal" },
                new[] { "environment", "--scope", "hostUser", "--reveal", "--reveal" },
                new[] { "apply-environment", "--id", Guid.NewGuid().ToString(), "--scope", "hostMachine" }
            })
            {
                try
                {
                    await SettingsCommands.RunAsync([.. args, "--server", "https://invalid.example"]);
                    throw new Exception("Invalid CLI options were accepted.");
                }
                catch (ArgumentException) { }
            }
            Console.WriteLine("Settings CLI: bounded JSON, BOM, empty/delete, secret-safe parse errors and invalid options passed. No HTTP requests or host mutations.");
        }
        finally { File.Delete(path); }
    }
}
