using Microsoft.Extensions.Configuration;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Startup;

internal static class ModuleHighTrustChecks
{
    internal static void Run(Action<bool, string> check)
    {
        void Accept((string Key, string? Value)[] values, string message)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                values.ToDictionary(item => item.Key, item => item.Value)).Build();
            ModuleHighTrustExtensionPolicy.Validate(configuration);
            check(true, message);
        }
        void Reject((string Key, string? Value)[] values, string message)
        {
            try
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                    values.ToDictionary(item => item.Key, item => item.Value)).Build();
                ModuleHighTrustExtensionPolicy.Validate(configuration);
                throw new Exception("Expected high-trust configuration rejection: " + message);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("high-trust", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("ModuleHighTrustExtensions", StringComparison.Ordinal))
            {
                check(true, message);
            }
        }

        Accept([], "high-trust configuration is absent by default and stays refused by contract");
        Accept([("ModuleHighTrustExtensions:Enabled", "false")],
            "an explicit high-trust Enabled=false deployment decision is accepted");
        Reject([("ModuleHighTrustExtensions:Enabled", "true")],
            "enabling unimplemented high-trust execution stops startup instead of silently accepting it");
        Reject([("ModuleHighTrustExtensions:Enabled", "perhaps")],
            "a non-boolean high-trust setting stops startup");
        Reject([("ModuleHighTrustExtensions:Allowed", "extension-a")],
            "an allowlist without an implemented execution boundary is refused");

        foreach (var kind in ModuleHighTrustExtensions.RefusedKinds)
        {
            check(!ModuleCapabilityCatalog.Kinds.Contains(kind, StringComparer.Ordinal),
                "high-trust kind is not part of the declarative capability catalog: " + kind);
            try
            {
                ModulePrivilegePlanStore.Validate(new ModulePrivilegeRequest(kind, "run", "fixture", "Run extension", "ht-" + kind));
                throw new Exception("Expected high-trust plan rejection: " + kind);
            }
            catch (ModulePrivilegeException error) when (error.Code == ModuleHighTrustExtensions.UnsupportedCode)
            {
                check(true, "high-trust request returns the stable explicit refusal: " + kind);
            }
        }

        var operationNames = Enum.GetNames<PrivilegedOperationKind>();
        check(operationNames.All(name => name is not ("ExecuteCommand" or "ProcessExecute" or "Shell" or "Command")),
            "the Helper protocol never exposes a generic command/process-execution operation");
        var requestProperties = typeof(PrivilegedOperationRequest).GetProperties()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        check(!requestProperties.Contains("Executable") && !requestProperties.Contains("Arguments")
            && !requestProperties.Contains("CommandLine") && !requestProperties.Contains("Argv"),
            "the Helper request contract has no executable/argv/command-line fields");
    }
}
