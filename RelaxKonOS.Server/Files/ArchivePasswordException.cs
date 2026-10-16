namespace RelaxKonOS.Server.Files;

public sealed class ArchivePasswordException(bool invalid = false) : IOException(invalid
    ? "The archive password is incorrect or the encrypted data is damaged."
    : "This archive requires a password.")
{
    public string Code => invalid ? "archive-password-invalid" : "archive-password-required";
}
