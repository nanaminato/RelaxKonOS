namespace RelaxKonOS.Server.Privileged;

/// <summary>Verifies a host administrator and returns its canonical audit identity, without retaining credentials or native tokens.</summary>
public interface IHostAdministratorAuthenticator
{
    HostAdministratorAuthenticationResult Authenticate(string currentUsername, string? administratorUsername, string? password);
}

public sealed record HostAdministratorAuthenticationResult(bool Succeeded, string ProblemCode, string AuthenticationMethod,
    string? AdministratorIdentity);
