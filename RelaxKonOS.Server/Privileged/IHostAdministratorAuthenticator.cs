namespace RelaxKonOS.Server.Privileged;

/// <summary>Verifies a host administrator without persisting credentials, tokens, or SIDs.</summary>
public interface IHostAdministratorAuthenticator
{
    HostAdministratorAuthenticationResult Authenticate(string currentUsername, string? administratorUsername, string? password);
}

public sealed record HostAdministratorAuthenticationResult(bool Succeeded, string ProblemCode, string AuthenticationMethod);
