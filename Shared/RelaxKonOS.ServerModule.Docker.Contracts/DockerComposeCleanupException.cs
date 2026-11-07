namespace RelaxKonOS.Server.Docker;

/// <summary>Native process or preview file cleanup could not be confirmed.</summary>
public sealed class DockerComposeCleanupException() : InvalidOperationException("Docker Compose cleanup could not be confirmed.");
