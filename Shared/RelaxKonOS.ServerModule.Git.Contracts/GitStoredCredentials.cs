namespace RelaxKonOS.Server.Git;

// Persisted schema types are Host-shared to keep serializer metadata out of collectible code.
    public sealed record StoredGitCredential(string Username, string ProtectedPassword, bool IsDefault);
    public sealed record StoredGitHostCredentials(string Host, List<StoredGitCredential> Accounts);
