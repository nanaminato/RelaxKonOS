using System.Text;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.MySql;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private const string MyRoot = "/var/lib/relaxkonos/mysql-control";
    private const string MyUser = "relaxkonos-mysql";
    private static readonly SemaphoreSlim MyGate = new(1);
    private sealed record MyIntent(Guid Id, MySqlRequest Request, Guid? Instance, string Phase);
    private sealed record MyLedger(long Revision, bool Installed, Guid? Instance, MyIntent? Pending,
        Dictionary<Guid, MySqlRequest> Receipts, IReadOnlyList<string>? Databases = null, IReadOnlyList<MySqlAccount>? Accounts = null);
    private static async Task<PrivilegedOperationResult> ExecuteMySqlAsync(PrivilegedOperationRequest request)
    {
        if (request != new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, MySql: request.MySql, MySqlSecrets: request.MySqlSecrets)
            || ((request.Operation == PrivilegedOperationKind.MySqlQuery) != (request.MySql is null)) || request.MySql is null && request.MySqlSecrets is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "database.request_invalid");

        if (!OperatingSystem.IsLinux() || !File.Exists("/etc/os-release")
            || !File.ReadAllLines("/etc/os-release").Contains("ID=ubuntu")
            || !File.ReadAllLines("/etc/os-release").Contains("VERSION_ID=\"24.04\"")
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64
            || !Directory.Exists("/run/systemd/system"))
            return MyReply(new(false, 0, false, null, false, false, null, null, "database.platform_unsupported"));

        await MyGate.WaitAsync();
        try
        {
            PgNoLinks(MyRoot);
            Directory.CreateDirectory(MyRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((await PgRun("/usr/bin/stat", ["-c", "%u:%a", MyRoot])).Output.Trim() != "0:700")
                throw new PgFailure("database.state_untrusted");
            PgNoLinks(MyRoot + "/lock");
            using var gate = new FileStream(MyRoot + "/lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = MyRoot + "/state.json";
            PgNoLinks(path);
            LinuxDirectorySync.Flush(MyRoot);
            if (File.Exists(path) && new FileInfo(path).Length > 2_097_152) throw new PgFailure("database.state_invalid");
            var ledger = File.Exists(path)
                ? JsonSerializer.Deserialize<MyLedger>(File.ReadAllBytes(path), PgJson) ?? throw new PgFailure("database.state_invalid")
                : new MyLedger(1, false, null, null, []);
            if (ledger.Revision < 1 || ledger.Instance == Guid.Empty || ledger.Instance is not null && !ledger.Installed
                || ledger.Receipts is null || ledger.Pending is { } saved &&
                (saved.Id == Guid.Empty || !saved.Request.Valid || saved.Request.Action is null))
                throw new PgFailure("database.state_invalid");
            var id = request.OperationId!.Value;
            if (request.MySql is null)
                return MyReply(await MyObserve(ledger, id));
            var intent = request.MySql;
            if (!intent.Valid || request.MySqlSecrets is not null && intent.Account is null && intent.RecoverOperationId is null) throw new PgFailure("database.request_invalid");
            if (ledger.Receipts.TryGetValue(id, out var committed))
            {
                if (committed != intent) throw new PgFailure("database.operation_conflict");
                return MyReply(await MyObserve(ledger, intent.RecoverOperationId ?? id));
            }
            if (ledger.Receipts.Count >= 10000)
                throw new PgFailure("database.state_capacity");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var packages = await AcquirePackageResourceLockAsync(deadline.Token);
            if (intent.RecoverOperationId is { } original)
            {
                if (ledger.Pending?.Id != original && !ledger.Receipts.ContainsKey(original))
                    throw new PgFailure("database.operation_unknown");
                if (ledger.Pending is { } another && another.Id != original)
                    throw new PgFailure("database.recovery_required");
            }
            else if (ledger.Pending is { } pending)
            {
                if (pending.Id != id || pending.Request != intent) throw new PgFailure("database.recovery_required");
            }
            else
            {
                if (ledger.Revision != intent.Revision)
                    throw new PgFailure("database.plan_changed");
                if (intent.Account is { } account)
                    await MyAccountPreflight(ledger, intent.Action!.Value, account, request.MySqlSecrets);
                else await MyPreflight(ledger, intent.Action!.Value);
                ledger = MySave(ledger with { Pending = new(id, intent,
                    intent.Action == MySqlAction.Initialize ? Guid.NewGuid() : ledger.Instance, "Prepared") });
            }
            if (ledger.Pending is not null)
            {
                if (ledger.Pending.Request.Account is not null)
                    ledger = await MyContinueAccount(ledger, request.MySqlSecrets);
                else
                {
                    if (request.MySqlSecrets is not null) throw new PgFailure("database.request_invalid");
                    ledger = await MyContinue(ledger);
                }
                var completed = ledger.Pending!;
                ledger.Receipts.Add(completed.Id, completed.Request);
                ledger = MySave(ledger with { Revision = ledger.Revision + 1, Pending = null });
            }
            if (intent.RecoverOperationId is not null)
            {
                ledger.Receipts.Add(id, intent);
                MySave(ledger);
            }
            return MyReply(await MyObserve(ledger, intent.RecoverOperationId ?? id));
        }
        catch (Exception error) when (error is PgFailure or IOException or UnauthorizedAccessException
            or JsonException or HttpRequestException or InvalidOperationException or OperationCanceledException
            or System.ComponentModel.Win32Exception)
        {
            return new(false, 65, Error: error is PgFailure ? error.Message : "database.operation_unavailable",
                ProblemCode: PrivilegedProblemCode.RecoveryRequired);
        }
        finally { MyGate.Release(); }
    }

    private static MyLedger MySave(MyLedger ledger)
    {
        PgAtomic(MyRoot + "/state.json", JsonSerializer.SerializeToUtf8Bytes(ledger, PgJson));
        return ledger;
    }

    private static MyLedger MyPhase(MyLedger ledger, string phase) =>
        MySave(ledger with { Pending = ledger.Pending! with { Phase = phase } });

    private static PrivilegedOperationResult MyReply(MySqlState state) =>
        new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state)));

    private static async Task MyPreflight(MyLedger ledger, MySqlAction action)
    {
        if (action == MySqlAction.Install)
        {
            if (ledger.Installed) throw new PgFailure("database.installation_exists");
            foreach (var path in new[] { "/etc/mysql", "/var/lib/mysql", "/var/lib/relaxkonos/mysql", MySqlBootstrapPolicy.Server, "/usr/bin/mysql" })
            {
                PgNoLinks(path);
                if (Directory.Exists(path) || File.Exists(path)) throw new PgFailure("database.external_installation");
            }
            var inventory = await PgRun("/usr/bin/dpkg-query", ["-W", "-f=${binary:Package}|${db:Status-Status}\\n", "mysql*", "mariadb*", "percona*"], allowFailure: true);
            if (inventory.Code is not (0 or 1) || inventory.Output.Split('\n').Any(x => x.Contains('|') && !x.EndsWith("|not-installed")))
                throw new PgFailure("database.external_installation");
            await MyAudit();
            return;
        }
        if (!ledger.Installed || (action == MySqlAction.Initialize) != (ledger.Instance is null))
            throw new PgFailure("database.instance_invalid");
        await MyVerifyPackages();
        if (action == MySqlAction.Initialize &&
            (await PgRun("/usr/bin/getent", ["passwd", MyUser], allowFailure: true)).Code != 2)
            throw new PgFailure("database.external_installation");
    }

    private static async Task MyAudit()
    {
        if ((await PgRun("/usr/bin/dpkg", ["--audit"])).Output.Trim().Length != 0)
            throw new PgFailure("database.package_recovery_required");
    }

    private static async Task MyVerifyPackages()
    {
        await MyAudit();
        foreach (var artifact in MySqlBootstrapPolicy.Artifacts)
        {
            var status = await PgRun("/usr/bin/dpkg-query", ["-W", "-f=${Status}|${Version}|${Architecture}", artifact.Name]);
            if (status.Output != $"install ok installed|{MySqlBootstrapPolicy.PackageVersion}|amd64")
                throw new PgFailure("database.package_identity_changed");
            var verification = await PgRun("/usr/bin/dpkg", ["--verify", artifact.Name]);
            if (verification.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
                !line.StartsWith("missing") || (!line.Contains(" /usr/share/doc/") && !line.Contains(" /usr/share/man/"))))
                throw new PgFailure("database.package_identity_changed");
        }
        foreach (var (file, owner) in new[] { (MySqlBootstrapPolicy.Server, "mysql-community-server-core"),
            ("/usr/bin/mysql", "mysql-community-client-core"), ("/usr/lib/mysql/plugin/auth_socket.so", "mysql-community-server-core") })
        {
            PgNoLinks(file);
            if ((await PgRun("/usr/bin/dpkg-query", ["-S", file])).Output.Trim() != owner + ": " + file)
                throw new PgFailure("database.package_identity_changed");
        }
    }

    private static async Task<string[]> MyPrepareArtifacts()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var http = new HttpClient(handler);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var paths = new List<string>();
        foreach (var artifact in MySqlBootstrapPolicy.Artifacts)
        {
            var path = MyRoot + "/" + artifact.FileName;
            PgNoLinks(path);
            using var response = await http.GetAsync(artifact.Uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != artifact.Bytes)
                throw new PgFailure("database.artifact_changed");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[checked((int)artifact.Bytes)];
            await source.ReadExactlyAsync(bytes, deadline.Token);
            if (await source.ReadAsync(new byte[1], deadline.Token) != 0 ||
                !PgHash(bytes).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PgFailure("database.artifact_changed");
            PgAtomic(path, bytes);
            paths.Add(path);
        }
        return paths.ToArray();
    }

    private static string MyUnit(MySqlBootstrapInstance plan) => "/etc/systemd/system/" + plan.ServiceName;
    private static async Task MyVerifyUnit(MySqlBootstrapInstance plan)
    {
        PgNoLinks(MyUnit(plan));
        PgNoLinks(plan.DataDirectory);
        PgNoLinks(plan.SocketDirectory);
        if (File.ReadAllText(MyUnit(plan)) != plan.UnitText)
            throw new PgFailure("database.configuration_drift");
        var identity = await PgRun("/usr/bin/systemctl", ["show", "-p", "FragmentPath", "-p", "DropInPaths", plan.ServiceName]);
        if (!identity.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order().SequenceEqual(
            new[] { "DropInPaths=", "FragmentPath=" + MyUnit(plan) }))
            throw new PgFailure("database.service_identity_changed");
    }

    private static async Task<string> MySqlQuery(MySqlBootstrapInstance plan, string sql) =>
        (await PgRun("/usr/bin/mysql", ["--no-defaults", "--protocol=SOCKET", "--socket=" + plan.SocketDirectory + "/mysql.sock",
            "--user=root", "--batch", "--skip-column-names", "--connect-timeout=2"], input: sql)).Output.Trim();

    private static async Task<bool> MyRunning(MySqlBootstrapInstance plan) =>
        (await PgRun("/usr/bin/systemctl", ["is-active", plan.ServiceName], allowFailure: true)).Code == 0;

    private static async Task MyVerifyConnection(MySqlBootstrapInstance plan)
    {
        await MyVerifyUnit(plan);
        var id = (await PgRun("/usr/bin/systemctl", ["show", "-p", "MainPID", "--value", plan.ServiceName])).Output.Trim();
        if (!int.TryParse(id, out var pid) || pid <= 1 ||
            File.ResolveLinkTarget($"/proc/{pid}/exe", true)?.FullName != MySqlBootstrapPolicy.Server)
            throw new PgFailure("database.service_identity_changed");
        var result = await MySqlQuery(plan, "SELECT CURRENT_USER(), VERSION(), @@datadir, @@skip_networking, plugin FROM mysql.user WHERE user='root' AND host='localhost';");
        if (result != $"root@localhost\t{MySqlBootstrapPolicy.Version}\t{plan.DataDirectory}/\t1\tauth_socket")
            throw new PgFailure("database.connection_unverified");
    }

    private static async Task MyWaitSocket(MySqlBootstrapInstance plan)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try { if (await MySqlQuery(plan, "SELECT CURRENT_USER();") == "root@localhost") return; }
            catch (PgFailure) { }
            await Task.Delay(200);
        }
        throw new PgFailure("database.connection_unverified");
    }

    private static async Task<MyLedger> MyContinue(MyLedger ledger)
    {
        var intent = ledger.Pending!;
        var action = intent.Request.Action!.Value;
        if (action == MySqlAction.Install)
        {
            if (intent.Phase == "Prepared")
            {
                await MyPreflight(ledger, action);
                var paths = await MyPrepareArtifacts();
                const string ubuntuSource = "deb [arch=amd64 signed-by=/usr/share/keyrings/ubuntu-archive-keyring.gpg,F6ECB3762474EDA9D21B7022871920D1991BC93C] http://archive.ubuntu.com/ubuntu ";
                PgAtomic(MyRoot + "/ubuntu.list", Encoding.UTF8.GetBytes(string.Concat(
                    new[] { "noble", "noble-updates", "noble-security" }.Select(suite => ubuntuSource + suite + " main universe\n"))));
                string[] apt = ["-o", "Dir::Etc::sourcelist=" + MyRoot + "/ubuntu.list", "-o", "Dir::Etc::sourceparts=-",
                    "-o", "Dir::State::lists=" + MyRoot + "/lists", "-o", "Dir::Cache::archives=" + MyRoot + "/archives",
                    "-o", "APT::Sandbox::User=root", "-o", "Acquire::AllowInsecureRepositories=false",
                    "-o", "APT::Get::AllowUnauthenticated=false", "-o", "Acquire::Check-Valid-Until=true"];
                Directory.CreateDirectory(MyRoot + "/lists");
                Directory.CreateDirectory(MyRoot + "/archives");
                await PgRun("/usr/bin/apt-get", [.. apt, "update", "-o", "APT::Update::Error-Mode=any"], minutes: 10);
                ledger = MyPhase(ledger, "PackageDispatched");
                await PgRun("/usr/bin/apt-get", [.. apt, "install", "--yes", "--no-remove", "--no-install-recommends", .. paths], minutes: 15);
            }
            await MyVerifyPackages();
            if (Directory.Exists("/var/lib/mysql")) throw new PgFailure("database.external_installation");
            return ledger with { Installed = true };
        }

        var plan = MySqlBootstrapPolicy.Plan(intent.Instance!.Value);
        if (action == MySqlAction.Initialize)
        {
            if (intent.Phase == "Prepared")
            {
                await MyPreflight(ledger, action);
                foreach (var path in new[] { MyUnit(plan), plan.SocketDirectory, plan.DataDirectory })
                {
                    PgNoLinks(path);
                    if (File.Exists(path) || Directory.Exists(path)) throw new PgFailure("database.external_installation");
                }
                ledger = MyPhase(ledger, "InitializeDispatched");
                await PgRun("/usr/sbin/useradd", ["--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", MyUser]);
                await PgRun("/usr/bin/install", ["-d", "-m", "0700", "-o", MyUser, "-g", MyUser, plan.DataDirectory]);
                await PgRun("/usr/sbin/runuser", ["-u", MyUser, "--", MySqlBootstrapPolicy.Server, .. plan.InitializeArguments], minutes: 5);
                ledger = MyPhase(ledger, "Initialized");
            }
            if (ledger.Pending!.Phase == "InitializeDispatched") throw new PgFailure("database.recovery_required");
            if (ledger.Pending.Phase == "Initialized")
            {
                PgNoLinks(MyUnit(plan));
                if (File.Exists(MyUnit(plan)) && File.ReadAllText(MyUnit(plan)) != plan.UnitText)
                    throw new PgFailure("database.configuration_drift");
                PgAtomic(MyUnit(plan), Encoding.UTF8.GetBytes(plan.UnitText));
                await PgRun("/usr/bin/systemctl", ["daemon-reload"]);
                await MyVerifyUnit(plan);
                ledger = MyPhase(ledger, "Securing");
            }
            await MyVerifyPackages();
            await MyVerifyUnit(plan);
            if (!await MyRunning(plan)) await PgRun("/usr/bin/systemctl", ["start", plan.ServiceName]);
            await MyWaitSocket(plan);
            if (await MySqlQuery(plan, "SELECT COUNT(*) FROM information_schema.plugins WHERE PLUGIN_NAME='auth_socket' AND PLUGIN_STATUS='ACTIVE';") == "0")
                await MySqlQuery(plan, "INSTALL PLUGIN auth_socket SONAME 'auth_socket.so';");
            await MySqlQuery(plan, "ALTER USER 'root'@'localhost' IDENTIFIED WITH auth_socket;");
            await MyVerifyConnection(plan);
            return ledger with { Instance = plan.Id };
        }
        await MyVerifyPackages();
        await MyVerifyUnit(plan);
        if (action == MySqlAction.Start)
        {
            if (!await MyRunning(plan)) await PgRun("/usr/bin/systemctl", ["start", plan.ServiceName]);
            await MyWaitSocket(plan);
        }
        if (action == MySqlAction.Stop)
        {
            await PgRun("/usr/bin/systemctl", ["stop", plan.ServiceName]);
            if ((await PgRun("/usr/bin/systemctl", ["show", "-p", "MainPID", "--value", plan.ServiceName])).Output.Trim() != "0")
                throw new PgFailure("database.stop_unconfirmed");
        }
        else await MyVerifyConnection(plan);
        return ledger;
    }

    private static async Task<MySqlState> MyObserve(MyLedger ledger, Guid operation)
    {
        var running = false;
        var verified = false;
        string? problem = ledger.Pending is null ? null : "database.recovery_required";
        if (ledger.Instance is { } id && ledger.Pending is null)
        {
            try
            {
                var plan = MySqlBootstrapPolicy.Plan(id);
                await MyVerifyPackages();
                await MyVerifyUnit(plan);
                running = await MyRunning(plan);
                if (running)
                {
                    await MyVerifyConnection(plan);
                    verified = true;
                }
            }
            catch (PgFailure error) { problem = error.Message; }
            catch (IOException) { problem = "database.state_unavailable"; }
        }
        return new(true, ledger.Revision, ledger.Installed, ledger.Instance, running, verified,
            ledger.Pending?.Id, ledger.Receipts.ContainsKey(operation) ? operation : null, problem, ledger.Databases, ledger.Accounts, ledger.Pending?.Request.Account);
    }
}
