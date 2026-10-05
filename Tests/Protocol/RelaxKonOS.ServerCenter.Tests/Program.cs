using RelaxKonOS.ServerCenter.Tests;

if (args.Contains("--json-contract-only"))
{
    ServerCenterContractChecks.VerifyJsonContract();
    return;
}

if (args.Contains("--linux-systems-only"))
{
    ServerCenterContractChecks.VerifyLinuxSystems();
    return;
}

ServerCenterContractChecks.Run();
InstallationOptionChecks.Run();
