using RelaxKonOS.Protocol.Tunnels;

namespace RelaxKonOS.ServerModule.Tunnels;

public interface ITunnelExecution
{
    string? ValidateProfile(UpsertTunnelServerProfileRequest request);
    string? ValidateDefinition(UpsertTunnelDefinitionRequest request);
    string GenerateConfiguration(TunnelServerProfileDto profile, IEnumerable<TunnelDefinitionDto> definitions, string? token);
}
