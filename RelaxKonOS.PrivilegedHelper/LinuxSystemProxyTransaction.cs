using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed record LinuxProxyTarget(string Provider, uint UserId = 0, string UserName = "", string Home = "");
internal interface ILinuxSystemProxyBackend
{
    IReadOnlyList<LinuxProxyTarget> DesktopTargets();
    Dictionary<string, string?> Read(LinuxProxyTarget target, IEnumerable<string> keys);
    void Write(LinuxProxyTarget target, IReadOnlyDictionary<string, string?> values);
    void Notify(LinuxProxyTarget target);
}

/// <summary>Durable ownership of only proxy keys. Unrelated settings and later external edits survive disabling.</summary>
internal sealed class LinuxSystemProxyTransaction(string journalPath, ILinuxSystemProxyBackend backend)
{
    internal sealed record Entry(LinuxProxyTarget Target, Dictionary<string, string?> Original,
        Dictionary<string, string?> Applied, Dictionary<string, string?>? Pending = null);
    private static readonly LinuxProxyTarget EnvironmentTarget = new("environment");

    public void Apply(LinuxSystemProxyConfiguration configuration)
    {
        if (configuration.Enabled) Validate(configuration);
        var journal = ReadJournal();
        var previous = journal.ToList();
        var undo = new List<(LinuxProxyTarget Target, Dictionary<string, string?> Values)>();
        try
        {
            if (configuration.Enabled)
            {
                var targets = new[] { EnvironmentTarget }.Concat(backend.DesktopTargets())
                    .Concat(journal.Select(entry => entry.Target)).Distinct().ToArray();
                foreach (var target in targets)
                {
                    var desired = Values(target.Provider, configuration);
                    var current = backend.Read(target, desired.Keys);
                    var index = journal.FindIndex(entry => entry.Target == target);
                    var entry = index < 0 ? new Entry(target, current, current) : journal[index];
                    if (!configuration.Enforce && current.Any(pair => !Owned(entry, pair.Key, pair.Value)))
                        throw new InvalidDataException("System proxy settings changed outside RelaxKonOS.");
                    if (index < 0) { index = journal.Count; journal.Add(entry); }
                    journal[index] = entry with { Pending = desired };
                    SaveJournal(journal); // Persist intent before any OS mutation, including the first enable.
                    undo.Add((target, current));
                    backend.Write(target, desired);
                    var observed = backend.Read(target, desired.Keys);
                    if (desired.Any(pair => !Equivalent(pair.Value, observed[pair.Key]))) throw new IOException("Proxy readback failed.");
                    journal[index] = entry with { Applied = observed };
                    SaveJournal(journal);
                    backend.Notify(target);
                }
            }
            else
            {
                for (var index = 0; index < journal.Count; index++)
                {
                    var entry = journal[index];
                    var current = backend.Read(entry.Target, entry.Original.Keys);
                    // Restore values still owned by us. A user's newer value is never erased.
                    var restore = entry.Original.Where(pair => Owned(entry, pair.Key, current[pair.Key]))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    undo.Add((entry.Target, current));
                    journal[index] = entry with { Pending = restore };
                    SaveJournal(journal);
                    backend.Write(entry.Target, restore);
                    var observed = backend.Read(entry.Target, entry.Original.Keys);
                    if (restore.Any(pair => !Equivalent(pair.Value, observed[pair.Key]))) throw new IOException("Proxy restore readback failed.");
                    var applied = new Dictionary<string, string?>(entry.Applied, StringComparer.Ordinal);
                    foreach (var key in restore.Keys) applied[key] = observed[key];
                    journal[index] = entry with { Applied = applied };
                    SaveJournal(journal);
                    backend.Notify(entry.Target);
                }
                if (File.Exists(journalPath)) File.Delete(journalPath);
            }
        }
        catch
        {
            var restored = true;
            foreach (var (target, values) in undo.AsEnumerable().Reverse())
            {
                try
                {
                    var index = journal.FindIndex(entry => entry.Target == target);
                    var entry = journal[index];
                    var current = backend.Read(target, values.Keys);
                    var restore = values.Where(pair => Owned(entry, pair.Key, current[pair.Key]))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    journal[index] = entry with { Pending = restore };
                    SaveJournal(journal);
                    backend.Write(target, restore);
                    var observed = backend.Read(target, values.Keys);
                    if (values.Any(pair => !Equivalent(pair.Value, observed[pair.Key]))) restored = false;
                    var applied = new Dictionary<string, string?>(entry.Applied, StringComparer.Ordinal);
                    foreach (var key in restore.Keys) applied[key] = observed[key];
                    journal[index] = entry with { Applied = applied };
                    SaveJournal(journal);
                    backend.Notify(target);
                }
                catch { restored = false; }
            }
            if (restored)
            {
                if (previous.Count == 0) { if (File.Exists(journalPath)) File.Delete(journalPath); }
                else SaveJournal(previous);
            }
            // Failed compensation retains the original values and pending writes for a later disable.
            throw;
        }
    }

    private static bool Owned(Entry entry, string key, string? value) =>
        entry.Applied.TryGetValue(key, out var applied) && Equivalent(applied, value)
        || entry.Pending is not null && entry.Pending.TryGetValue(key, out var pending) && Equivalent(pending, value);

    internal static bool Equivalent(string? left, string? right) => Canonical(left) == Canonical(right);
    private static string? Canonical(string? value) => value == "@as []" ? "[]" : value;

    internal static void Validate(LinuxSystemProxyConfiguration configuration)
    {
        if (configuration.Port is < 1 or > 65535 || !IPAddress.TryParse(configuration.Host, out var address)
            || !(IPAddress.IsLoopback(address) || NetworkInterface.GetAllNetworkInterfaces().Any(network =>
                network.GetIPProperties().UnicastAddresses.Any(local => local.Address.Equals(address))))
            || configuration.BypassList.Length > 4096 || configuration.BypassList.Any(char.IsControl))
            throw new ArgumentException("Invalid Linux proxy configuration.");
        foreach (var token in Tokens(configuration))
            if (token.Length > 256 || token.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '/' or '-' or '_')))
                throw new ArgumentException("Linux bypass entries must be hosts, domains or network addresses.");
    }

    private static string[] Tokens(LinuxSystemProxyConfiguration configuration) =>
        (configuration.UseDefaultBypass ? new[] { "localhost", "127.0.0.1", "::1" } : [])
        .Concat(configuration.BypassList.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static Dictionary<string, string?> Values(string provider, LinuxSystemProxyConfiguration configuration)
    {
        var http = new UriBuilder("http", configuration.Host, configuration.Port).Uri.GetLeftPart(UriPartial.Authority);
        var socks = new UriBuilder("socks5", configuration.Host, configuration.Port).Uri.GetLeftPart(UriPartial.Authority);
        var bypass = Tokens(configuration);
        if (provider == "environment") return new(StringComparer.Ordinal)
        {
            ["HTTP_PROXY"] = http, ["http_proxy"] = http, ["HTTPS_PROXY"] = http, ["https_proxy"] = http,
            ["ALL_PROXY"] = socks, ["all_proxy"] = socks, ["NO_PROXY"] = string.Join(',', bypass), ["no_proxy"] = string.Join(',', bypass)
        };
        if (provider == "gnome") return new(StringComparer.Ordinal)
        {
            ["http/host"] = Variant(configuration.Host), ["http/port"] = configuration.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["http/use-authentication"] = "false", ["https/host"] = Variant(configuration.Host), ["https/port"] = configuration.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["socks/host"] = Variant(configuration.Host), ["socks/port"] = configuration.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ftp/host"] = "''", ["ftp/port"] = "0",
            ["use-same-proxy"] = "false",
            ["ignore-hosts"] = "[" + string.Join(", ", bypass.Select(Variant)) + "]", ["mode"] = "'manual'"
        };
        if (provider == "kde") return new(StringComparer.Ordinal)
        {
            ["httpProxy"] = http, ["httpsProxy"] = http, ["socksProxy"] = socks.Replace("socks5://", "socks://", StringComparison.Ordinal),
            ["ftpProxy"] = "", ["NoProxyFor"] = string.Join(',', bypass), ["ReversedException"] = "false", ["ProxyType"] = "1"
        };
        throw new InvalidDataException("Unsupported proxy provider.");
    }
    private static string Variant(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private List<Entry> ReadJournal()
    {
        var directory = Path.GetDirectoryName(journalPath)!;
        if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException();
        if (!File.Exists(journalPath)) return [];
        if (File.GetAttributes(journalPath).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(journalPath).Length > 1024 * 1024)
            throw new InvalidDataException("Invalid proxy journal.");
        var result = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(journalPath)) ?? throw new InvalidDataException("Invalid proxy journal.");
        if (result.Count > 33 || result.Any(entry => entry is null || entry.Target is null)
            || result.Select(entry => entry.Target).Distinct().Count() != result.Count) throw new InvalidDataException("Invalid proxy journal.");
        var sample = new LinuxSystemProxyConfiguration(true, "127.0.0.1", 7890, true, "");
        foreach (var entry in result)
        {
            if (entry is null || entry.Target is null || entry.Original is null || entry.Applied is null)
                throw new InvalidDataException("Invalid proxy journal entry.");
            var keys = Values(entry.Target.Provider, sample).Keys.ToHashSet(StringComparer.Ordinal);
            if (!keys.SetEquals(entry.Original.Keys) || !keys.SetEquals(entry.Applied.Keys)
                || entry.Pending?.Keys.Any(key => !keys.Contains(key)) == true) throw new InvalidDataException("Invalid proxy journal keys.");
        }
        return result;
    }

    private void SaveJournal(List<Entry> entries)
    {
        var directory = Path.GetDirectoryName(journalPath)!;
        Directory.CreateDirectory(directory);
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException();
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(directory, ".proxy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) { JsonSerializer.Serialize(file, entries); file.Flush(true); }
            File.Move(temporary, journalPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
