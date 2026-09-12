namespace RelaxKonOS.Server.Identity;

/// <summary>Native account APIs cannot be cancelled. Timed-out workers keep their slot until they exit,
/// and can return only identity data, never mutate credentials or issue sessions.</summary>
public sealed class BoundedIdentityProvider(IIdentityProvider native) : IIdentityProvider
{
    private readonly SemaphoreSlim workers = new(4, 4);
    private T Run<T>(Func<T> action, T unavailable)
    {
        if (!workers.Wait(0)) return unavailable;
        var task = Task.Run(() => { try { return action(); } catch { return unavailable; } finally { workers.Release(); } });
        try { return task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { return unavailable; }
    }
    public CredentialVerifyResult Verify(string username, string password)
        => Run(() => native.Verify(username, password), CredentialVerifyResult.Failed("Identity service unavailable", CredentialError.Unknown));
    public IdentityLookup Lookup(string identifier) => Run(() => native.Lookup(identifier), new IdentityLookup(IdentityLookupStatus.Unavailable));
    public IdentityLookup LookupIdentity(string identity) => Run(() => native.LookupIdentity(identity), new IdentityLookup(IdentityLookupStatus.Unavailable));
    public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity) => Run(() => native.CheckAliasEligibility(identity), new AliasEligibility(false, "account-state-unavailable"));
    public PlatformUserInfo GetUserInfo(string username) => Lookup(username).Identity ?? throw new InvalidOperationException("Identity service unavailable.");
}
