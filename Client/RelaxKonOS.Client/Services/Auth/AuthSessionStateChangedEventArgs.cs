namespace RelaxKonOS.Client.Services.Auth;

public sealed class AuthSessionStateChangedEventArgs : EventArgs
{
    public AuthSessionState State { get; }
    public RememberedProfileSaveResult? RememberedProfileSaveResult { get; }
    public AuthSessionEndReason EndReason { get; }

    public AuthSessionStateChangedEventArgs(AuthSessionState state,
        RememberedProfileSaveResult? rememberedProfileSaveResult = null,
        AuthSessionEndReason endReason = AuthSessionEndReason.None)
    {
        State = state;
        RememberedProfileSaveResult = rememberedProfileSaveResult;
        EndReason = endReason;
    }
}
