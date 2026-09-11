namespace RelaxKonOS.Protocol.Docker;

/// <summary>Non-executing runtime installation/startup plan. Host elevation remains outside RelaxKonOS.</summary>
public sealed record DockerInstallationPlanDto(bool CanProceed, string ProblemCode, IReadOnlyList<string> Steps, IReadOnlyList<string> Warnings);
