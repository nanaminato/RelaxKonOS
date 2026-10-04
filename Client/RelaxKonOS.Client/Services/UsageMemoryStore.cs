using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;

namespace RelaxKonOS.Client.Services;

/// <summary>Device-local interaction defaults. Never stores secrets or grants.</summary>
public sealed class UsageMemoryStore
{
    private readonly object gate = new();
    private readonly string path;
    private Dictionary<string, Dictionary<string, string>> accounts = new();
    private long generation;

    public UsageMemoryStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "usage-memory.json")) { }
    public UsageMemoryStore(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path)) accounts = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path)) ?? new();
            if (accounts.Any(x => x.Value is null)) accounts = new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static string? Account(IAuthSession session) => session.State == AuthSessionState.Authenticated
        && session.ServiceId is { Length: > 0 } service && session.CurrentUser is { } user
        ? JsonSerializer.Serialize(new { service, user = user.Id }) : null;

    public UsageMemoryScope Capture(IAuthSession session)
    {
        lock (gate) return new(this, session, Account(session), session.CurrentWorkspace?.Id, generation);
    }

    public static UsageMemoryScope Capture(RelaxKonOS.AppSDK.AppContext context)
        => CaptureServices(context.Services);

    public static UsageMemoryScope CaptureServices(IServiceProvider services)
        => ((UsageMemoryStore)services.GetService(typeof(UsageMemoryStore))!).Capture(
            (IAuthSession)services.GetService(typeof(IAuthSession))!);

    internal string? Read(UsageMemoryScope scope, string key)
    {
        lock (gate) return scope.Account is not null && scope.Generation == generation && IsCurrent(scope) && accounts.TryGetValue(scope.Account!, out var values)
            && values.TryGetValue(key, out var value) ? value : null;
    }

    internal void Write(UsageMemoryScope scope, string key, string? value)
    {
        lock (gate)
        {
            if (scope.Account is null || scope.Generation != generation || !IsCurrent(scope) || string.IsNullOrWhiteSpace(value)) return;
            if (!accounts.TryGetValue(scope.Account!, out var values)) accounts[scope.Account!] = values = new();
            values[key] = value;
            Persist();
        }
    }

    internal bool IsCurrent(UsageMemoryScope scope) => scope.Session.CurrentSession?.Id == scope.SessionId && Account(scope.Session) == scope.Account && scope.Session.CurrentWorkspace?.Id == scope.Workspace;

    public void Clear(IAuthSession session)
    {
        lock (gate)
        {
            var account = Account(session);
            if (account is null) return;
            accounts.Remove(account);
            generation++;
            Persist();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(accounts));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("Could not persist usage memory: " + e.GetType().Name); }
    }
}

public sealed class UsageMemoryScope : IUsageMemoryScope
{
    private readonly UsageMemoryStore store;
    internal IAuthSession Session { get; }
    internal string? Account { get; }
    internal Guid? Workspace { get; }
    internal long Generation { get; }
    internal Guid? SessionId { get; }
    internal UsageMemoryScope(UsageMemoryStore store, IAuthSession session, string? account, Guid? workspace, long generation)
    {
        (this.store, Session, Account, Workspace, Generation) = (store, session, account, workspace, generation);
        SessionId = session.CurrentSession?.Id;
    }
    public bool IsCurrent => store.IsCurrent(this);
    public string? Administrator => store.Read(this, "administrator");
    public void RememberAdministrator(string? username) => store.Write(this, "administrator", username?.Trim());
    private string DirectoryKey(string purpose, bool remote) => remote ? $"remote:{Workspace}:{purpose}" : "local:" + purpose;
    public string? Directory(string purpose, bool remote) => store.Read(this, DirectoryKey(purpose, remote));
    public void RememberDirectory(string purpose, bool remote, string? directory) => store.Write(this, DirectoryKey(purpose, remote), directory);
}
