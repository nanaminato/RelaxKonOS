using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.ServerModule.Tunnels;

public sealed class TunnelExecution : ITunnelExecution
{
    public string? ValidateProfile(UpsertTunnelServerProfileRequest request) =>
        TunnelValidation.ValidateProfile(request.Name, request.Host, request.Port, request.AuthKind, request.RuntimeMode, request.ExternalExecutablePath);

    public string? ValidateDefinition(UpsertTunnelDefinitionRequest request) =>
        TunnelValidation.ValidateDefinition(request.Name, request.Protocol, request.LocalHost, request.LocalPort, request.RemotePort, request.Domain);

    public string GenerateConfiguration(TunnelServerProfileDto profile, IEnumerable<TunnelDefinitionDto> definitions, string? token) =>
        FrpTomlGenerator.Generate(profile, definitions, token);
}
