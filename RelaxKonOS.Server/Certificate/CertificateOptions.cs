namespace RelaxKonOS.Server.Certificate;

public sealed class CertificateOptions
{
    public string DirectoryUrl { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";
    public string? StorageRoot { get; set; }
    public string? ChallengeRoot { get; set; }
    public int RenewalFallbackDays { get; set; } = 30;
    public int RenewalRetryMaxAttempts { get; set; } = 6;
    public int RenewalRetryBaseDelayMinutes { get; set; } = 1;
    public int VersionRetentionCount { get; set; } = 3;
}
