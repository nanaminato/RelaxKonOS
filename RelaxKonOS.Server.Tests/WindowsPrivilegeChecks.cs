using Microsoft.AspNetCore.Http;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Server.Endpoints;
using System.Security.AccessControl;

internal static class WindowsPrivilegeChecks
{
    public static async Task RunAsync(string root)
    {
        var nginx = Path.Combine(root, "webserver", "managed-nginx");
        var privateRoot = Path.Combine(root, "private-runtime");
        var policy = WindowsManagedRuntimePolicy.Create(nginx, privateRoot, [Path.Combine(root, "archives")], [], null);
        policy.Validate();
        var parse = typeof(HostAdministratorAuthenticator).GetMethod("ParseUsername", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var (name, expectedAccount, expectedDomain) in new (string, string, string?)[]
        {
            ("administrator@example.test", "administrator@example.test", null),
            (@"MicrosoftAccount\user@example.test", "user@example.test", "MicrosoftAccount"),
            (@"HOST\administrator", "administrator", "HOST"),
        })
        {
            object?[] args = [name, null, null]; parse.Invoke(null, args);
            TestAssert.Assert(Equals(args[1], expectedAccount) && Equals(args[2], expectedDomain), "Windows administrator account parsing changed the canonical logon name.");
        }
        TestAssert.Assert(WindowsRuntimeConfigurationWriter.IsValidRequest(new(ManagedRuntime.Frpc, ManagedRuntimeAction.Stop, ProfileId: Guid.NewGuid())), "A closed stop request was rejected.");
        foreach (var malformed in new ManagedRuntimeRequest[]
        {
            new(ManagedRuntime.Nginx, ManagedRuntimeAction.Stop, Version: "1.31.3"),
            new(ManagedRuntime.Frpc, ManagedRuntimeAction.Status, ProfileId: Guid.Empty),
            new(ManagedRuntime.Frpc, ManagedRuntimeAction.Stop, ProfileId: Guid.NewGuid(), ArchivePath: "anything"),
            new(ManagedRuntime.Frps, ManagedRuntimeAction.Status, Version: "v0.71.0"),
            new(ManagedRuntime.Frps, ManagedRuntimeAction.Install, Version: "v0.71.0"),
        }) TestAssert.Assert(!WindowsRuntimeConfigurationWriter.IsValidRequest(malformed), "An unrelated runtime field/action was accepted.");
        TestAssert.Assert(!WindowsManagedRuntimePolicy.Contains(nginx, nginx + "-other" + Path.DirectorySeparatorChar + "nginx.exe"), "A sibling directory crossed the runtime boundary.");
        TestAssert.Assert(!WindowsManagedRuntimePolicy.Contains(nginx, Path.Combine(nginx, "..", "escaped.exe")), "Parent traversal crossed the runtime boundary.");
        ExpectDenied(() => (policy with { ArchiveRoots = [nginx] }).Validate(), "Archive ingress overlapped executable storage.");
        ExpectDenied(() => (policy with { NginxRoot = Path.Combine(Path.GetPathRoot(root)!, "nginx") }).Validate(), "Nginx would protect a whole volume root.");
        ExpectDenied(() => (policy with { FrpReleases = [WindowsManagedRuntimeDefaults.FrpReleases[0] with { Url = "https://github.com/attacker/frp/releases/download/v0.71.0/frp.zip" }] }).Validate(), "An untrusted release URL was accepted.");

        var validConfig = "events { worker_connections 128; } http { include mime.types; include relaxkonos.d/*.conf; server { listen 8080; location / { root html; } } }";
        WindowsRuntimeConfigurationWriter.ValidateNginx(validConfig, nginx);
        foreach (var malicious in new[]
        {
            "load_module evil.dll;", "load\\_module evil.dll;", "\"load_module\" evil.dll;",
            "env PATH;", "http { include ../../outside.conf; }", "http { include ../secrets.json; }",
            "http { include *.json; }", "http { access_log ../arbitrary.conf; }", "http { lua_code_cache off; }",
            "http { include \"unterminated; }",
            "pid logs/other.pid;", "http { proxy_store ../../outside; }", "http { dav_methods PUT; }",
            "http { ssl_conf_command Engine evil; }",
        }) ExpectDenied(() => WindowsRuntimeConfigurationWriter.ValidateNginx(malicious, nginx), "Unsafe Nginx configuration was accepted: " + malicious);

        var client = new FrpcServiceConfiguration("example.test", 7000, TunnelTlsMode.Force, "quote-\"-token",
            [new("api", TunnelProtocol.Tcp, "127.0.0.1", 8080, 18080, null, true, true)]);
        var generated = WindowsRuntimeConfigurationWriter.Client(client, null);
        TestAssert.Assert(generated.Contains("token = \"quote-\\\"-token\"", StringComparison.Ordinal), "A credential was not escaped in closed FRP TOML.");
        ExpectDenied(() => WindowsRuntimeConfigurationWriter.Client(client with { Host = "host\n[plugin]" }, null), "FRP host allowed TOML injection.");
        ExpectDenied(() => WindowsRuntimeConfigurationWriter.Client(client with { Token = "{{ .Envs.SECRET }}" }, null), "FRP environment expansion was accepted.");
        ExpectDenied(() => WindowsRuntimeConfigurationWriter.Client(client with { Proxies = [client.Proxies[0] with { LocalPort = 0 }] }, null), "An invalid FRP port was accepted.");

        var transport = new CapturingPrivilegedTransport();
        await new PrivilegedNginxOperations(transport).ApplyWindowsRuntimeAsync(ManagedRuntimeAction.Install, "1.31.3");
        TestAssert.Assert(transport.LastRequest is { Operation: PrivilegedOperationKind.ManagedRuntime, Path: null, ServiceId: null,
            ManagedRuntime.Runtime: ManagedRuntime.Nginx, ManagedRuntime.Action: ManagedRuntimeAction.Install }, "Nginx bypassed the closed Helper runtime request.");
        var unavailable = new ManagedRuntimeOperations(new SystemAuthenticationTransport(new(false, ProblemCode: PrivilegedProblemCode.HelperUnavailable)));
        TestAssert.Assert(!(await unavailable.ExecuteAsync(new(ManagedRuntime.Frps, ManagedRuntimeAction.Start))).Success, "Missing Helper silently succeeded.");
        if (OperatingSystem.IsWindows())
        {
            VerifyStaticSiteAccess(root, policy);
            await VerifyGrantsAsync();
            await VerifyUntrustedRuntimeAsync(policy);
        }
        Console.WriteLine("Windows privilege policy and authorization checks passed.");
    }

    private static void ExpectDenied(Action operation, string message)
    {
        try { operation(); }
        catch (Exception error) when (error is UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void VerifyStaticSiteAccess(string root, WindowsManagedRuntimePolicy policy)
    {
        var site = Path.Combine(root, "public-site");
        var nested = Path.Combine(site, "assets");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "index.html");
        File.WriteAllText(file, "site fixture");
        var worker = new System.Security.Principal.SecurityIdentifier("S-1-5-80-101-102-103-104-105");
        var reader = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalServiceSid, null);
        var nestedInfo = new DirectoryInfo(nested);
        var nestedSecurity = nestedInfo.GetAccessControl();
        nestedSecurity.SetAccessRuleProtection(true, true);
        nestedInfo.SetAccessControl(nestedSecurity);
        var info = new FileInfo(file);
        var before = info.GetAccessControl();
        before.SetAccessRuleProtection(true, true);
        before.PurgeAccessRules(worker);
        before.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(reader,
            System.Security.AccessControl.FileSystemRights.WriteData, System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(before);
        var owner = before.GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        WindowsNginxStaticSiteAccess.GrantContentAccess([site, nested], [file], worker, [reader]);
        var after = info.GetAccessControl();
        TestAssert.Assert(Equals(owner, after.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))), "Static-site grant changed ownership.");
        TestAssert.Assert(after.AreAccessRulesProtected, "Static-site grant changed inheritance protection.");
        var fileRules = after.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToArray();
        TestAssert.Assert(fileRules.Any(rule => rule.IdentityReference.Equals(worker)
            && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
            && (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadAndExecute) == System.Security.AccessControl.FileSystemRights.ReadAndExecute),
            "Nginx cannot read a file with protected inheritance after the grant.");
        TestAssert.Assert(fileRules.Where(rule => rule.IdentityReference.Equals(worker)).All(rule =>
            (rule.FileSystemRights & (System.Security.AccessControl.FileSystemRights.Write
                | System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership)) == 0),
            "Static-site grant added write or ownership permissions for Nginx.");
        TestAssert.Assert(fileRules.Any(rule => rule.IdentityReference.Equals(reader)
            && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Deny), "Static-site grant removed an existing deny rule.");
        var directoryRules = new DirectoryInfo(site).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Where(rule => rule.IdentityReference.Equals(reader)).ToArray();
        TestAssert.Assert(directoryRules.Any(rule => (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadAttributes) != 0),
            "Server cannot validate static-root metadata after the grant.");
        TestAssert.Assert(directoryRules.All(rule => (rule.FileSystemRights & (System.Security.AccessControl.FileSystemRights.Write
            | System.Security.AccessControl.FileSystemRights.ReadData | System.Security.AccessControl.FileSystemRights.ChangePermissions)) == 0),
            "Server received content or write access instead of metadata and traversal access.");
        TestAssert.Assert(nestedInfo.GetAccessControl().AreAccessRulesProtected, "Static-site grant changed child-directory inheritance protection.");
        var futureFile = Path.Combine(nested, "future.html");
        File.WriteAllText(futureFile, "new content");
        TestAssert.Assert(new FileInfo(futureFile).GetAccessControl().GetAccessRules(false, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().Any(rule => rule.IdentityReference.Equals(worker)
                && rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                && (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadAndExecute) == System.Security.AccessControl.FileSystemRights.ReadAndExecute),
            "Future static content did not inherit Nginx read access.");
        Directory.CreateDirectory(policy.PrivateRoot);
        foreach (var invalid in new[] { policy.PrivateRoot, root, Path.GetPathRoot(site)!, @"\\server\share", "relative/site" })
            ExpectDenied(() => WindowsNginxStaticSiteAccess.Grant(invalid, policy), "Unsafe static-site root accepted: " + invalid);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task VerifyUntrustedRuntimeAsync(WindowsManagedRuntimePolicy policy)
    {
        // A caller can pre-create matching binary hashes; that must never be sufficient to run elevated.
        Directory.CreateDirectory(policy.NginxRoot);
        var binary = Path.Combine(policy.NginxRoot, "nginx.exe");
        var bytes = Encoding.UTF8.GetBytes("caller-controlled executable");
        await File.WriteAllBytesAsync(binary, bytes);
        await File.WriteAllTextAsync(Path.Combine(policy.NginxRoot, ".helper-integrity.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["nginx.exe"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) }));
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var directory = new DirectoryInfo(policy.NginxRoot);
        var acl = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User!,
            System.Security.AccessControl.FileSystemRights.Write, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.SetAccessControl(directory, acl);
        var result = await WindowsManagedRuntimeHost.ExecuteAsync(new(ManagedRuntime.Nginx, ManagedRuntimeAction.Start), policy);
        TestAssert.Assert(!result.Success && result.ProblemCode == PrivilegedProblemCode.ResourceNotAllowed, "Caller-created runtime hashes bypassed ownership/ACL validation.");
    }

    private static async Task VerifyGrantsAsync()
    {
        var owner = Guid.NewGuid().ToString("D"); var id = Guid.NewGuid();
        var profile = new TunnelServerProfileDto(id, "managed", "example.test", 7000, TunnelAuthKind.Token, true,
            TunnelTlsMode.Force, TunnelRuntimeMode.Managed, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var service = new ProfileService(owner, profile);
        var context = new DefaultHttpContext { User = Principal(owner, "access-a") };
        var grants = new HostElevationSessionStore(new TestHostAccountPrivilegeService(), new UploadSessionChecks.SystemMode(), new HostElevationSessionState()); var calls = 0;
        Task<TunnelOperationResultDto> Operation() { calls++; return Task.FromResult(new TunnelOperationResultDto(true, TunnelConnectionState.Starting)); }
        var method = typeof(TunnelEndpoints).GetMethod("ProfileLifecycleAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        Task<IResult> Invoke() => (Task<IResult>)method.Invoke(null, [id, context, service, grants, (Func<Task<TunnelOperationResultDto>>)Operation, CancellationToken.None])!;
        TestAssert.Assert((await Invoke() as IStatusCodeHttpResult)?.StatusCode == 403 && calls == 0, "Unapproved FRP invoked an operation.");
        grants.Grant(context.User, HostElevationCapability.FrpInstall, id.ToString("D"), false, "test");
        grants.Grant(context.User, HostElevationCapability.FrpLifecycle, Guid.NewGuid().ToString("D"), false, "test");
        TestAssert.Assert((await Invoke() as IStatusCodeHttpResult)?.StatusCode == 403 && calls == 0, "Wrong capability/resource authorized FRP.");
        var expires = grants.Grant(context.User, HostElevationCapability.FrpLifecycle, id.ToString("D"), false, "test");
        TestAssert.Assert(expires > DateTimeOffset.UtcNow && expires <= DateTimeOffset.UtcNow.AddMinutes(5), "Runtime grant had the wrong lifetime.");
        await Invoke(); await Invoke();
        TestAssert.Assert(calls == 2, "A valid short grant was not reused.");
        context.User = Principal(owner, "access-b");
        TestAssert.Assert((await Invoke() as IStatusCodeHttpResult)?.StatusCode == 403 && calls == 2, "Grant leaked across access tokens.");
        context.User = Principal(Guid.NewGuid().ToString("D"), "access-a");
        TestAssert.Assert((await Invoke() as IStatusCodeHttpResult)?.StatusCode == 404 && calls == 2, "Another user operated an owned profile.");
        context.User = Principal(owner, "access-a"); grants.Revoke(context.User);
        TestAssert.Assert((await Invoke() as IStatusCodeHttpResult)?.StatusCode == 403 && calls == 2, "Revoked grant authorized FRP.");
        var frps = typeof(TunnelEndpoints).GetMethod("FrpsLifecycleAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var denied = await (Task<IResult>)frps.Invoke(null, [context, grants, (Func<Task<TunnelOperationResultDto>>)Operation])!;
        TestAssert.Assert((denied as IStatusCodeHttpResult)?.StatusCode == 403 && calls == 2, "frps used a profile grant.");
        grants.Grant(context.User, HostElevationCapability.FrpLifecycle, "frps", false, "test");
        await (Task<IResult>)frps.Invoke(null, [context, grants, (Func<Task<TunnelOperationResultDto>>)Operation])!;
        TestAssert.Assert(calls == 3, "Exact frps grant was ignored.");
    }

    private static ClaimsPrincipal Principal(string user, string jti) => new(new ClaimsIdentity([
        new Claim("sub", user), new Claim(ClaimTypes.NameIdentifier, user), new Claim("jti", jti)], "test"));

    private sealed class ProfileService(string owner, TunnelServerProfileDto profile) : ITunnelService
    {
        public Task<TunnelServerProfileDto?> GetProfileAsync(Guid id, string user, CancellationToken ct) => Task.FromResult(id == profile.Id && user == owner ? profile : null);
        public Task<IReadOnlyList<TunnelServerProfileDto>> ListProfilesAsync(string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelServerProfileDto> UpsertProfileAsync(Guid? id, UpsertTunnelServerProfileRequest request, string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteProfileAsync(Guid id, string user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetProfileTokenAsync(Guid id, string token, string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<TunnelDefinitionDto>> ListTunnelsAsync(string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelDefinitionDto?> GetTunnelAsync(Guid id, string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<TunnelDefinitionDto> UpsertTunnelAsync(Guid? id, UpsertTunnelDefinitionRequest request, string user, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteTunnelAsync(Guid id, string user, CancellationToken ct) => throw new NotSupportedException();
    }
}
