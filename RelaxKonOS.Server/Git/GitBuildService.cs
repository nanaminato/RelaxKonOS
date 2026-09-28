using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Git;

public sealed class GitBuildOptions
{
    public string RootDirectory { get; set; } = "data/git-builds";
    public string BuilderName { get; set; } = "";
    public string BuilderContainer { get; set; } = "";
    public string BuilderNetwork { get; set; } = "";
    public string[] AllowedHosts { get; set; } = ["github.com", "gitlab.com"];
    public long MaximumMemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public double MaximumCpuCores { get; set; } = 2;
    public int MaximumPids { get; set; } = 512;
    public int TimeoutMinutes { get; set; } = 20;
}

public sealed class GitBuildProblem(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>Protected credentials and a durable, bounded build ledger. An unfinished build becomes
/// interrupted at startup; a retry with the same key returns that record and never reruns it.</summary>
public sealed class GitBuildService : IDisposable
{
    private sealed record Credential(Guid Id, Guid Owner, string Name, string ProtectedToken);
    private sealed record BuildEntry(Guid Owner, string KeyHash, string RequestHash, GitBuildOperationDto Operation);
    private sealed record Ledger(Credential[] Credentials, BuildEntry[] Builds);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Regex Sha = new("\\A[a-fA-F0-9]{40}\\z", RegexOptions.Compiled);
    private static readonly Regex Ref = new("\\A(refs/(heads|tags)/)?[A-Za-z0-9][A-Za-z0-9._/-]{0,199}\\z", RegexOptions.Compiled);
    private static readonly Regex Builder = new("\\A[a-z][a-z0-9-]{0,39}\\z", RegexOptions.Compiled);
    private readonly object gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> active = new();
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly GitBuildOptions options;
    private Ledger ledger = new([], []);
    private bool unavailable;

    public GitBuildService(IHostEnvironment environment, IDataProtectionProvider protection, GitBuildOptions options)
    {
        this.options = options;
        path = Path.Combine(environment.ContentRootPath, options.RootDirectory, "builds.json");
        protector = protection.CreateProtector("RelaxKonOS.GitBuild.Credentials.v1");
        try
        {
            if (!File.Exists(path)) return;
            ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), Json) ?? throw new JsonException();
            if (ledger.Credentials is null || ledger.Builds is null
                || ledger.Credentials.Select(x => x.Id).Distinct().Count() != ledger.Credentials.Length
                || ledger.Builds.Select(x => x.Operation.Id).Distinct().Count() != ledger.Builds.Length)
                throw new JsonException();
            var interrupted = ledger.Builds.Select(entry => entry.Operation.State is "queued" or "running"
                ? entry with { Operation = entry.Operation with
                    { State = "interrupted", ProblemCode = "git-build.interrupted", FinishedAt = DateTimeOffset.UtcNow } }
                : entry).ToArray();
            if (!interrupted.SequenceEqual(ledger.Builds)) Save(new(ledger.Credentials, interrupted));
        }
        catch { unavailable = true; }
    }

    public GitBuildCredentialDto[] Credentials(Guid owner)
    {
        lock (gate) { Ensure(); return [.. ledger.Credentials.Where(x => x.Owner == owner).Select(x => new GitBuildCredentialDto(x.Id, x.Name))]; }
    }

    public GitBuildCredentialDto SetCredential(Guid owner, GitBuildCredentialRequest request)
    {
        if (request.Name is not { Length: >= 1 and <= 80 } || string.IsNullOrWhiteSpace(request.Name)
            || request.Token is not { Length: >= 1 and <= 4096 }
            || request.Name.Any(char.IsControl) || request.Token.Any(char.IsControl))
            throw new GitBuildProblem("git-build.invalid-credential", 400);
        lock (gate)
        {
            Ensure();
            if (ledger.Credentials.Any(x => x.Owner == owner && x.Name == request.Name.Trim()))
                throw new GitBuildProblem("git-build.credential-name-conflict");
            var entry = new Credential(Guid.NewGuid(), owner, request.Name.Trim(), protector.Protect(request.Token));
            Save(new([.. ledger.Credentials, entry], ledger.Builds));
            return new(entry.Id, entry.Name);
        }
    }

    public async Task<GitBuildResolvedDto> ResolveAsync(Guid owner, GitBuildResolveRequest request, CancellationToken ct)
    {
        var url = ValidUrl(request.RepositoryUrl);
        var reference = ValidRef(request.Reference);
        if (request.CredentialId is not null && new Uri(url).Host != "github.com")
            throw new GitBuildProblem("git-build.private-host-unsupported", 400);
        var token = Token(owner, request.CredentialId);
        var env = GitEnvironment(token);
        var result = await RunAsync("git", ["ls-remote", "--exit-code", url, reference], env, TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode != 0) throw new GitBuildProblem("git-build.ref-unavailable");
        var matches = result.Lines.Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && Sha.IsMatch(parts[0]) && parts[1] == reference).ToArray();
        if (matches.Length != 1) throw new GitBuildProblem("git-build.ref-unavailable");
        // Annotated tags point at tag objects. Resolve the peeled commit instead of accepting an
        // ambiguous tag object as a build input.
        if (reference.StartsWith("refs/tags/", StringComparison.Ordinal))
        {
            var peeled = await RunAsync("git", ["ls-remote", "--exit-code", url, reference + "^{}"], env, TimeSpan.FromSeconds(30), ct);
            var line = peeled.Lines.FirstOrDefault();
            if (peeled.ExitCode == 0 && line is not null && line.Length >= 40 && Sha.IsMatch(line[..40]))
                return new(url, reference, line[..40].ToLowerInvariant());
        }
        return new(url, reference, matches[0][0].ToLowerInvariant());
    }

    public async Task<GitBuildRefDto[]> RefsAsync(Guid owner, GitBuildResolveRequest request, CancellationToken ct)
    {
        var url = ValidUrl(request.RepositoryUrl);
        if (request.CredentialId is not null && new Uri(url).Host != "github.com")
            throw new GitBuildProblem("git-build.private-host-unsupported", 400);
        var result = await RunAsync("git", ["ls-remote", url, "refs/heads/*", "refs/tags/*"],
            GitEnvironment(Token(owner, request.CredentialId)), TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode != 0) throw new GitBuildProblem("git-build.ref-unavailable");
        return [.. result.Lines.Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && Sha.IsMatch(parts[0])
                && (parts[1].StartsWith("refs/heads/", StringComparison.Ordinal)
                    || parts[1].StartsWith("refs/tags/", StringComparison.Ordinal))
                && !parts[1].EndsWith("^{}", StringComparison.Ordinal))
            .Take(200).Select(parts => new GitBuildRefDto(parts[1], parts[0].ToLowerInvariant()))];
    }

    public GitBuildOperationDto[] Builds(Guid owner)
    {
        lock (gate) { Ensure(); return [.. ledger.Builds.Where(x => x.Owner == owner).Select(x => x.Operation)
            .OrderByDescending(x => x.CreatedAt).Take(200)]; }
    }

    public GitBuildOperationDto? Get(Guid owner, Guid id)
    {
        lock (gate) { Ensure(); return ledger.Builds.FirstOrDefault(x => x.Owner == owner && x.Operation.Id == id)?.Operation; }
    }

    public GitBuildOperationDto Artifact(Guid owner, Guid id)
    {
        var operation = Get(owner, id) ?? throw new GitBuildProblem("git-build.not-found", 404);
        if (operation.State != "succeeded" || operation.ImageReference is null || operation.ImageId is null)
            throw new GitBuildProblem("git-build.artifact-unavailable");
        return operation;
    }

    public async Task<GitBuildOperationDto> StartAsync(Guid owner, GitBuildRequest request, string key, CancellationToken ct)
    {
        if (key is not { Length: >= 8 and <= 128 } || key.Any(char.IsControl)) throw new GitBuildProblem("git-build.idempotency-required", 400);
        var url = ValidUrl(request.RepositoryUrl);
        var reference = ValidRef(request.Reference);
        if (request.CredentialId is not null && new Uri(url).Host != "github.com")
            throw new GitBuildProblem("git-build.private-host-unsupported", 400);
        if (string.IsNullOrWhiteSpace(request.CommitSha) || !Sha.IsMatch(request.CommitSha))
            throw new GitBuildProblem("git-build.invalid-sha", 400);
        var context = ValidPath(request.ContextDirectory, allowRoot: true);
        var dockerfile = ValidPath(request.Dockerfile, allowRoot: false);
        var canonical = request with { RepositoryUrl = url, Reference = reference, CommitSha = request.CommitSha.ToLowerInvariant(), ContextDirectory = context, Dockerfile = dockerfile };
        var requestHash = Hash(JsonSerializer.Serialize(canonical, Json));
        var keyHash = Hash(owner.ToString("D") + "\n" + key);
        lock (gate)
        {
            Ensure();
            var replay = ledger.Builds.FirstOrDefault(x => x.KeyHash == keyHash);
            if (replay is not null)
            {
                if (replay.Owner != owner || replay.RequestHash != requestHash) throw new GitBuildProblem("git-build.idempotency-conflict");
                return replay.Operation;
            }
        }
        await CheckBuilderAsync(ct);
        var resolved = await ResolveAsync(owner, new(url, reference, canonical.CredentialId), ct);
        if (!string.Equals(resolved.CommitSha, canonical.CommitSha, StringComparison.Ordinal))
            throw new GitBuildProblem("git-build.ref-moved");
        GitBuildOperationDto operation;
        lock (gate)
        {
            Ensure();
            var previous = ledger.Builds.FirstOrDefault(x => x.KeyHash == keyHash);
            if (previous is not null)
            {
                if (previous.Owner != owner || previous.RequestHash != requestHash) throw new GitBuildProblem("git-build.idempotency-conflict");
                return previous.Operation;
            }
            if (active.Count != 0) throw new GitBuildProblem("git-build.busy");
            _ = Token(owner, canonical.CredentialId);
            operation = new(Guid.NewGuid(), url, reference, canonical.CommitSha, context, dockerfile,
                "queued", null, null, null, DateTimeOffset.UtcNow, null, []);
            // Keep minimal old entries so a delayed retry can never allocate a second build.
            var old = ledger.Builds.Select((entry, index) => index < ledger.Builds.Length - 199
                ? entry with { Operation = entry.Operation with { Logs = [] } } : entry);
            Save(new(ledger.Credentials, [.. old, new BuildEntry(owner, keyHash, requestHash, operation)]));
            active.Add(operation.Id, new CancellationTokenSource());
        }
        _ = Task.Run(() => ExecuteAsync(owner, canonical, operation.Id), CancellationToken.None);
        return operation;
    }

    public GitBuildOperationDto Cancel(Guid owner, Guid id)
    {
        lock (gate)
        {
            Ensure();
            var entry = ledger.Builds.FirstOrDefault(x => x.Owner == owner && x.Operation.Id == id)
                ?? throw new GitBuildProblem("git-build.not-found", 404);
            if (active.TryGetValue(id, out var source)) source.Cancel();
            return entry.Operation;
        }
    }

    private async Task ExecuteAsync(Guid owner, GitBuildRequest request, Guid id)
    {
        var image = $"relaxkonos-git:build-{id:N}";
        var succeeded = false;
        try
        {
            Update(id, operation => operation with { State = "running" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Clamp(options.TimeoutMinutes, 1, 120)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, active[id].Token);
            var ct = linked.Token;
            await CheckBuilderAsync(ct);
            var token = Token(owner, request.CredentialId);
            var uri = request.RepositoryUrl + "?ref=" + Uri.EscapeDataString(request.CommitSha)
                + "&checksum=" + request.CommitSha;
            if (request.ContextDirectory != ".") uri += "&subdir=" + Uri.EscapeDataString(request.ContextDirectory);
            var args = new List<string> { "buildx", "build", "--builder", options.BuilderName, "--progress", "plain",
                "--pull", "--load", "--tag", image, "--file", request.Dockerfile };
            var env = new Dictionary<string, string>();
            if (token is not null)
            {
                args.AddRange(["--secret", "id=GIT_AUTH_TOKEN.github.com,env=GIT_AUTH_TOKEN"]);
                env["GIT_AUTH_TOKEN"] = token;
            }
            args.Add(uri);
            var output = await RunAsync("docker", args, env, TimeSpan.FromMinutes(Math.Clamp(options.TimeoutMinutes, 1, 120)), ct,
                batch => Append(id, batch, token));
            if (output.ExitCode != 0) throw new GitBuildProblem("git-build.build-failed");
            var inspect = await RunAsync("docker", ["image", "inspect", "--format", "{{.Id}}", image], new Dictionary<string, string>(), TimeSpan.FromSeconds(20), ct);
            var imageId = inspect.Lines.FirstOrDefault()?.Trim();
            if (inspect.ExitCode != 0 || imageId is null || !Regex.IsMatch(imageId, "^sha256:[a-f0-9]{64}$"))
                throw new GitBuildProblem("git-build.image-unavailable");
            Update(id, operation => operation with { State = "succeeded", ImageReference = image, ImageId = imageId, FinishedAt = DateTimeOffset.UtcNow });
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            var cancelled = active.TryGetValue(id, out var source) && source.IsCancellationRequested;
            Update(id, operation => operation with { State = cancelled ? "cancelled" : "failed",
                ProblemCode = cancelled ? "git-build.cancelled" : "git-build.timeout", FinishedAt = DateTimeOffset.UtcNow });
        }
        catch (GitBuildProblem error)
        {
            Update(id, operation => operation with { State = "failed", ProblemCode = error.Code, FinishedAt = DateTimeOffset.UtcNow });
        }
        catch (Exception)
        {
            Update(id, operation => operation with { State = "failed", ProblemCode = "git-build.execution-failed", FinishedAt = DateTimeOffset.UtcNow });
        }
        finally
        {
            if (!succeeded)
            {
                try { await RunAsync("docker", ["image", "rm", image], new Dictionary<string, string>(), TimeSpan.FromSeconds(15), CancellationToken.None); }
                catch (Exception) { /* Only this operation's unique tag may be removed. */ }
            }
            lock (gate) { if (active.Remove(id, out var source)) source.Dispose(); }
        }
    }

    private async Task CheckBuilderAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(options.BuilderName)
            || string.IsNullOrWhiteSpace(options.BuilderContainer) || !Builder.IsMatch(options.BuilderName)
            || !Builder.IsMatch(options.BuilderContainer) || string.IsNullOrWhiteSpace(options.BuilderNetwork)
            || !Builder.IsMatch(options.BuilderNetwork)
            || options.BuilderNetwork is "host" or "bridge" or "none" or "default")
            throw new GitBuildProblem("git-build.builder-unconfigured", 503);
        var details = await RunAsync("docker", ["buildx", "inspect", options.BuilderName], new Dictionary<string, string>(), TimeSpan.FromSeconds(15), ct);
        if (details.ExitCode != 0 || !details.Lines.Any(line => line.TrimStart().StartsWith("Driver:", StringComparison.Ordinal)
            && line.Contains("remote", StringComparison.Ordinal))
            || !details.Lines.Any(line => line.Contains("docker-container://" + options.BuilderContainer, StringComparison.Ordinal)))
            throw new GitBuildProblem("git-build.builder-unavailable", 503);
        var inspect = await RunAsync("docker", ["inspect", "--format", "{{json .HostConfig}}", options.BuilderContainer], new Dictionary<string, string>(), TimeSpan.FromSeconds(15), ct);
        if (inspect.ExitCode != 0 || inspect.Lines.Count != 1) throw new GitBuildProblem("git-build.builder-unavailable", 503);
        var identity = await RunAsync("docker", ["inspect", "--format", "{{.Config.User}}", options.BuilderContainer], new Dictionary<string, string>(), TimeSpan.FromSeconds(15), ct);
        var user = identity.Lines.FirstOrDefault()?.Trim();
        if (identity.ExitCode != 0 || string.IsNullOrWhiteSpace(user) || user is "0" or "root" || user.StartsWith("0:", StringComparison.Ordinal))
            throw new GitBuildProblem("git-build.builder-unrestricted", 503);
        var running = await RunAsync("docker", ["inspect", "--format", "{{.State.Running}}", options.BuilderContainer], new Dictionary<string, string>(), TimeSpan.FromSeconds(15), ct);
        if (running.ExitCode != 0 || running.Lines.FirstOrDefault()?.Trim() != "true")
            throw new GitBuildProblem("git-build.builder-unavailable", 503);
        try
        {
            using var document = JsonDocument.Parse(inspect.Lines[0]);
            var host = document.RootElement;
            long number(string name) => host.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
            var memory = number("Memory");
            var pids = number("PidsLimit");
            var nanoCpus = number("NanoCpus");
            var quota = number("CpuQuota");
            var period = number("CpuPeriod");
            var cpu = nanoCpus > 0 ? nanoCpus / 1_000_000_000d : period > 0 && quota > 0 ? (double)quota / period : 0;
            if (host.GetProperty("Privileged").GetBoolean() || memory <= 0 || memory > options.MaximumMemoryBytes
                || pids <= 0 || pids > options.MaximumPids || cpu <= 0 || cpu > options.MaximumCpuCores
                || host.GetProperty("NetworkMode").GetString() != options.BuilderNetwork
                || host.TryGetProperty("Binds", out var binds) && binds.ValueKind == JsonValueKind.Array && binds.GetArrayLength() != 0
                || host.TryGetProperty("CapAdd", out var caps) && caps.ValueKind == JsonValueKind.Array && caps.GetArrayLength() != 0
                || host.TryGetProperty("PidMode", out var pidMode) && pidMode.GetString() == "host")
                throw new GitBuildProblem("git-build.builder-unrestricted", 503);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new GitBuildProblem("git-build.builder-unavailable", 503);
        }
    }

    private string ValidUrl(string value)
    {
        if (value is not { Length: >= 12 and <= 512 } || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns || uri.Host is "localhost" or "local"
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || options.AllowedHosts?.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) != true
            || !uri.AbsolutePath.EndsWith(".git", StringComparison.Ordinal)
            || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            throw new GitBuildProblem("git-build.invalid-repository-url", 400);
        return uri.GetLeftPart(UriPartial.Path);
    }

    private static string ValidRef(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Ref.IsMatch(value) || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal) || value.EndsWith('/'))
            throw new GitBuildProblem("git-build.invalid-ref", 400);
        return value.StartsWith("refs/", StringComparison.Ordinal) ? value : "refs/heads/" + value;
    }

    private static string ValidPath(string value, bool allowRoot)
    {
        if (allowRoot && value == ".") return value;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.StartsWith('/') || value.Contains('\\')
            || value.Split('/').Any(part => part is "" or "." or ".." || part.Any(char.IsControl)))
            throw new GitBuildProblem("git-build.invalid-path", 400);
        return value;
    }

    private string? Token(Guid owner, Guid? id)
    {
        if (id is null) return null;
        lock (gate)
        {
            Ensure();
            var entry = ledger.Credentials.FirstOrDefault(x => x.Owner == owner && x.Id == id)
                ?? throw new GitBuildProblem("git-build.credential-not-found", 404);
            try { return protector.Unprotect(entry.ProtectedToken); }
            catch (CryptographicException) { throw new GitBuildProblem("git-build.credential-unavailable", 503); }
        }
    }

    private static Dictionary<string, string> GitEnvironment(string? token)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_COUNT"] = token is null ? "1" : "2",
            ["GIT_CONFIG_KEY_0"] = "http.followRedirects",
            ["GIT_CONFIG_VALUE_0"] = "false",
        };
        if (token is not null)
        {
            env["GIT_CONFIG_KEY_1"] = "http.extraHeader";
            env["GIT_CONFIG_VALUE_1"] = "Authorization: Basic "
                + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        }
        return env;
    }

    private static async Task<(int ExitCode, List<string> Lines)> RunAsync(string file, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env, TimeSpan timeout, CancellationToken cancellationToken,
        Action<IReadOnlyList<string>>? onOutput = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var process = new Process { StartInfo = new ProcessStartInfo(file) { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        foreach (var pair in env) process.StartInfo.Environment[pair.Key] = pair.Value;
        try { if (!process.Start()) throw new GitBuildProblem("git-build.tool-unavailable", 503); }
        catch (System.ComponentModel.Win32Exception) { throw new GitBuildProblem("git-build.tool-unavailable", 503); }
        var lines = new List<string>();
        var pending = new List<string>();
        async Task drain(StreamReader reader)
        {
            var buffer = new char[4096];
            var current = new StringBuilder(1024);
            void emit()
            {
                var line = current.ToString();
                current.Clear();
                string[]? batch = null;
                lock (lines)
                {
                    if (lines.Count < 4000) lines.Add(line);
                    if (onOutput is not null)
                    {
                        pending.Add(line);
                        if (pending.Count >= 20) { batch = [.. pending]; pending.Clear(); }
                    }
                }
                if (batch is not null) onOutput!(batch);
            }
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), limit.Token)) != 0)
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n') emit();
                    else if (buffer[index] != '\r' && current.Length < 1024) current.Append(buffer[index]);
                }
            if (current.Length != 0) emit();
        }
        try
        {
            await Task.WhenAll(drain(process.StandardOutput), drain(process.StandardError), process.WaitForExitAsync(limit.Token));
            if (pending.Count != 0) onOutput?.Invoke([.. pending]);
        }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        return (process.ExitCode, lines);
    }

    private void Append(Guid id, IEnumerable<string> lines, string? token)
    {
        Update(id, operation => operation with { Logs = [.. operation.Logs.Concat(lines.Select(line => SanitizeLine(line, token)))
            .TakeLast(120)] });
    }

    private static string SanitizeLine(string line, string? token)
    {
        if (token is not null)
        {
            line = line.Replace(token, "[redacted]", StringComparison.Ordinal);
            line = line.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token)),
                "[redacted]", StringComparison.Ordinal);
        }
        line = line.Replace("Authorization:", "[redacted-header]:", StringComparison.OrdinalIgnoreCase);
        line = new string(line.Where(character => !char.IsControl(character) || character == '\t').ToArray());
        return line.Length <= 512 ? line : line[..512];
    }

    private void Update(Guid id, Func<GitBuildOperationDto, GitBuildOperationDto> update)
    {
        lock (gate)
        {
            if (unavailable) return;
            Save(new(ledger.Credentials, [.. ledger.Builds.Select(entry => entry.Operation.Id == id
                ? entry with { Operation = update(entry.Operation) } : entry)]));
        }
    }

    private void Save(Ledger next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(path + ".tmp", path, overwrite: true);
            ledger = next;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            unavailable = true;
            throw new GitBuildProblem("git-build.store-unavailable", 503);
        }
    }

    private void Ensure() { if (unavailable) throw new GitBuildProblem("git-build.store-unavailable", 503); }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() { lock (gate) { foreach (var source in active.Values) source.Cancel(); } }
}
