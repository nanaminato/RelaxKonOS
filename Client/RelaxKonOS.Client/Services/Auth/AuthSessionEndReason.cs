namespace RelaxKonOS.Client.Services.Auth;

/// <summary>Why an authenticated desktop session ended.</summary>
public enum AuthSessionEndReason
{
    None,
    UserSignedOut,
    RefreshTokenInvalid,
}
