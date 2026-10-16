using System.Text;

namespace RelaxKonOS.Protocol.Files;

/// <summary>Request-only secret. Never put this in operation snapshots, URLs or persisted state.</summary>
public sealed class ArchiveCredentials(string password)
{
    public string Password { get; } = password;
    public override string ToString() => "[archive credentials redacted]";
    public void Validate()
    {
        if (string.IsNullOrEmpty(Password) || Password.Length > 1024 || Password.Any(c => c is '\0' or '\r' or '\n'))
            throw new ArgumentException("Archive password must contain 1–1024 characters without NUL or newlines.");
        try { _ = new UTF8Encoding(false, true).GetByteCount(Password); }
        catch (EncoderFallbackException) { throw new ArgumentException("Archive password contains invalid Unicode."); }
    }
    public void ValidateForCreation(FileArchiveFormat format)
    {
        Validate();
        if (format == FileArchiveFormat.Zip && (Password.Length > 99 || Password.Any(c => c < ' ' || c > '~')))
            throw new ArgumentException("ZIP creation requires 1–99 printable ASCII password characters. Use 7z for Unicode passwords.");
    }
}
