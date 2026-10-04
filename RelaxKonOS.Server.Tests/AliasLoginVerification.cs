using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;

internal static class AliasLoginVerification
{
    private const string OsPassword = "test OS password only";
    private const string AliasPassword = "a unique alias passphrase 2026";
    private static readonly JsonSerializerOptions Json = RelaxKonOSJsonOptions.Default;
    private static int checks;

    // Fixed P-256 key pair, nonce and signature produced once by a JVM (SunEC):
    //   Signature.getInstance("SHA256withECDSA") over the bytes 0x00..0x1F.
    // They pin the owner-device wire contract to what Android actually emits, instead of letting the
    // test and the service agree with each other while both stay incompatible with the phone.
    private const string AndroidPrivateKeyPkcs8 =
        "MEECAQAwEwYHKoZIzj0CAQYIKoZIzj0DAQcEJzAlAgEBBCCmdv0HBHvTyBwBoTzq3LLBrDPw0JKbUlptNikbdzkoCw==";
    private const string AndroidPublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE57fWRm+M5rQCgvJlzdE69iAuVFDIHP+XOJwpglzqK1y1ABSsACN+JPajQm2P386eXLN9GgoevcxWU860aUqQNg==";
    private const string AndroidVectorNonceBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string AndroidVectorSignatureBase64 =
        "MEQCICbHLxt2SP5QrnCPh0Amb+7caepIe3IJsZWx2Ih8sDEHAiA5/aJA7Yah4sUM473QOKIImfKX6hFKZ0O5xz1YI8sa6A==";

    public static async Task RunAsync(string root)
    {
        var provider = new FakeIdentityProvider();
        VerifyPasswords();
        VerifyMigration(root, provider);
        if (OperatingSystem.IsWindows())
        {
            var windows = new WindowsLogonProvider();
            var own = windows.Lookup(Environment.UserName);
            Check(own.Identity?.Uid.StartsWith("S-1-", StringComparison.Ordinal) == true, "native Windows lookup returns SID");
            Check(windows.LookupIdentity(own.Identity!.Uid).Identity?.Uid == own.Identity.Uid, "native Windows SID reverse lookup");
            var eligibility = windows.CheckAliasEligibility(own.Identity);
            Console.WriteLine($"Windows current-account read-only qualification: {eligibility.Available}, {eligibility.Reason ?? "local account eligible"}. No OS account was changed.");
        }
        var path = Path.Combine(root, "alias-http.db");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var services = builder.Services;
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        services.AddDbContext<RelaxKonOSDbContext>(options => options.UseSqlite("Data Source=" + path));
        services.AddScoped<IUserRepository, SqliteUserRepository>();
        services.AddScoped<IAliasCredentialRepository, SqliteAliasCredentialRepository>();
        services.AddScoped<IWorkspaceRepository, SqliteWorkspaceRepository>();
        services.AddScoped<IDeviceRepository, SqliteDeviceRepository>();
        services.AddScoped<IOwnerDeviceKeyRepository, SqliteOwnerDeviceKeyRepository>();
        services.AddSingleton<IRegistryRepository, InMemoryRegistryRepository>();
        services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
        services.AddScoped<IAuthenticationProtectionStore, SqliteAuthenticationProtectionStore>();
        services.AddSingleton<IIdentityProvider>(provider);
        // Auth endpoints and the login service resolve the deployment mode boundary; the host must
        // register the same contract the production Program does, or endpoint inference fails.
        services.AddSingleton<RelaxKonOS.Server.HostMode.IServerModeResolver>(
            new RelaxKonOS.Server.HostMode.ServerModeResolver(builder.Configuration, UserExecutionBackend.Helper));
        services.AddSingleton<AuthenticationGate>();
        services.AddSingleton<AuthSessionStore>();
        services.AddSingleton<AliasPasswordService>();
        services.AddSingleton<SessionValidityService>();
        services.AddSingleton(sp => new OwnerDeviceKeyService(sp.GetRequiredService<IServiceScopeFactory>(), workstation: true));
        services.AddSingleton(new RelaxKonOS.Server.EventAlerts.EventAlertsOptions());
        services.AddSingleton<RelaxKonOS.Server.BackupRecovery.IBackupRecoveryKeyProvider>(
            new RelaxKonOS.Server.BackupRecovery.ConfigurationBackupRecoveryKeyProvider(new RelaxKonOS.Server.BackupRecovery.BackupRecoveryOptions()));
        services.AddSingleton<JwtTokenService>();
        services.AddScoped<LoginProtectionService>();
        services.AddScoped<CanonicalUserResolver>();
        services.AddScoped<LoginAuthenticationService>();
        services.AddScoped<AliasCredentialService>();
        services.AddSingleton<HostElevationSessionState>();
        services.AddScoped<IHostElevationSessionStore, HostElevationSessionStore>();
        services.Configure<AuthSecurityOptions>(options => options.IpFailureLimit = 1000);
        var jwt = new JwtOptions { Secret = "alias-test-signing-secret-01234567890123456789" };
        services.Configure<JwtOptions>(options => { options.Secret = jwt.Secret; });
        services.AddAuthentication(RelaxKonOSAuthSchemes.User).AddJwtBearer(RelaxKonOSAuthSchemes.User, options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = jwt.Issuer,
                ValidateAudience = true, ValidAudience = jwt.Audience, ValidateLifetime = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)), ClockSkew = TimeSpan.Zero };
            options.Events = new JwtBearerEvents { OnTokenValidated = context =>
            {
                if (context.Principal!.HasClaim(RelaxKonOSAuthSchemes.TokenTypeClaim, RelaxKonOSAuthSchemes.FileCapabilityTokenType)
                    || !context.HttpContext.RequestServices.GetRequiredService<SessionValidityService>().IsValid(context.Principal)) context.Fail("Invalid session");
                return Task.CompletedTask;
            } };
        });
        services.AddAuthorization();
        services.AddRateLimiter(options => options.AddFixedWindowLimiter("login", policy => { policy.PermitLimit = 1000; policy.Window = TimeSpan.FromSeconds(1); }));
        await using var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>();
            db.Database.EnsureCreated(); IdentityMigrationRunner.Migrate(db, provider); IdentityMigrationRunner.Migrate(db, provider);
            // A database created by a prior server made this display-label pair unique. Verify
            // startup migration removes that constraint before any Android invitation is handled.
            db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_devices_Name_Platform");
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IX_devices_Name_Platform ON devices(Name, Platform)");
            IdentityMigrationRunner.Migrate(db, provider);
        }
        app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization(); app.MapAuthEndpoints(); app.MapAliasEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        async Task<HttpResponseMessage> Send(HttpMethod method, string route, object? body = null, string? token = null)
        {
            using var request = new HttpRequestMessage(method, route);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request);
        }
        async Task<LoginResponse> Login(string identifier, string password, ClientPlatformKind platform = ClientPlatformKind.Windows)
        {
            using var response = await Send(HttpMethod.Post, AuthApiRoutes.Login, new LoginRequest(identifier, password, platform, "test-device", "1"));
            Check(response.StatusCode == HttpStatusCode.OK, "login succeeds: " + identifier + ": " + response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<LoginResponse>(Json))!;
        }
        async Task<AliasConfigurationDto> Read(string token)
        {
            using var response = await Send(HttpMethod.Get, AuthApiRoutes.LoginAlias, token: token);
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "own configuration and no-store");
            return (await response.Content.ReadFromJsonAsync<AliasConfigurationDto>(Json))!;
        }
        var system = await Login("nanami", OsPassword);
        // 登录不因身份不合格而失败，但必须**声明**该身份能否执行普通操作：这是把"打开文件夹才发现
        // 503"前移到登录期的唯一手段，缺了它客户端只能事后解释。
        Check(system.ExecutionEligibility is { } declared && declared.Available == (declared.Reason is null),
            "login declares whether this identity may execute ordinary operations");
        var otherPlatform = await Login("HOST\\nanami", OsPassword, ClientPlatformKind.Android);
        Check(system.User.Id == otherPlatform.User.Id && system.Workspace.Id == otherPlatform.Workspace.Id, "equivalent OS identity and client platforms preserve workspace");
        Check(system.User.Platform == HostPlatformKind.Windows, "User uses host platform");
        var token = system.Tokens.AccessToken;
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Check(parsed.Claims.Single(x => x.Type == "sid").Value == system.Session.Id.ToString(), "domain session equals JWT sid");
        if (app.Services.GetRequiredService<OwnerDeviceKeyService>().IsAvailable)
        {
            var ownerDevices = app.Services.GetRequiredService<OwnerDeviceKeyService>();
            using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var ownerDeviceId = Guid.NewGuid();
            var registered = ownerDevices.Register(system.User.Id, ownerDeviceId,
                new OwnerDeviceBootstrapRequest("owner-test", "windows", Convert.ToBase64String(ownerKey.ExportSubjectPublicKeyInfo()), "1"));
            var challenge = ownerDevices.CreateChallenge(ownerDeviceId);
            var challengeNonce = DecodeBase64Url(challenge.Nonce);
            var verified = ownerDevices.VerifyChallenge(challenge.ChallengeId, ownerDeviceId,
                Convert.ToBase64String(ownerKey.SignData(challengeNonce, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
            Check(verified.Id == registered.Id && verified.LastUsedAt is not null,
                "RFC 3279 DER owner-device nonce signature is accepted exactly through its enrolled key");
            var ownerPrincipal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", system.User.Id.ToString()), new Claim("device_id", ownerDeviceId.ToString()),
                new Claim("amr", "owner-device-key"), new Claim("role", "controller"),
            ], "test"));
            Check(ownerDevices.IsOwner(ownerPrincipal),
                "nonce-signed owner-device controller session may use passwordless elevation");
            using var recoveredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var recovered = ownerDevices.RegisterOrReplaceLocalWindowsDevice(system.User.Id, ownerDeviceId,
                new OwnerDeviceBootstrapRequest("owner-test", "windows", Convert.ToBase64String(recoveredKey.ExportSubjectPublicKeyInfo()), "2"));
            Check(recovered.Id == ownerDeviceId && recovered.PublicKeySpki == Convert.ToBase64String(recoveredKey.ExportSubjectPublicKeyInfo()),
                "local Windows recovery replaces a lost key for its existing device");
            Check(ownerDevices.List(ownerPrincipal).Single().Id == ownerDeviceId,
                "SQLite lists active owner-device keys without DateTimeOffset query translation");
            var invitation = ownerDevices.CreateInvitation(ownerPrincipal);
            // Android interop: both wire values below come from a real JVM (SunEC) key pair, the same
            // provider family Android Keystore and Conscrypt use. Android emits ASN.1 DER signatures,
            // so a server that verifies .NET's default IEEE P1363 concatenation rejects every phone.
            using var invitedAndroidKey = ECDsa.Create();
            // Windows CNG cannot import SunEC's PKCS#8 encoding when the EC private structure
            // omits its public point. Supply the corresponding JVM public point explicitly.
            invitedAndroidKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(AndroidPublicKeySpki), out _);
            var androidParameters = invitedAndroidKey.ExportParameters(false);
            var privateReader = new System.Formats.Asn1.AsnReader(Convert.FromBase64String(AndroidPrivateKeyPkcs8), System.Formats.Asn1.AsnEncodingRules.DER).ReadSequence();
            _ = privateReader.ReadInteger();
            _ = privateReader.ReadSequence();
            var ecPrivate = new System.Formats.Asn1.AsnReader(privateReader.ReadOctetString(), System.Formats.Asn1.AsnEncodingRules.DER).ReadSequence();
            _ = ecPrivate.ReadInteger();
            androidParameters.D = ecPrivate.ReadOctetString();
            invitedAndroidKey.ImportParameters(androidParameters);
            var androidDeviceId = Guid.Empty;
            using (var response = await Send(HttpMethod.Post, OwnerDeviceKeyApiRoutes.AcceptInvitation,
                       new OwnerDeviceAcceptInvitationRequest(invitation.Token, "test-device", "android", AndroidPublicKeySpki, "3")))
            {
                Check(response.StatusCode == HttpStatusCode.Created,
                    "owner invitation enrollment accepts an Android label already used by another device: " +
                    (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync());
                androidDeviceId = (await response.Content.ReadFromJsonAsync<OwnerDeviceDto>(Json))!.Id;
            }
            using (var challengeResponse = await Send(HttpMethod.Post, OwnerDeviceKeyApiRoutes.Challenge,
                       new OwnerDeviceChallengeRequest(androidDeviceId)))
            {
                Check(challengeResponse.StatusCode == HttpStatusCode.OK, "the enrolled Android device obtains a nonce");
                var androidChallenge = (await challengeResponse.Content.ReadFromJsonAsync<OwnerDeviceChallenge>(Json))!;
                using var signIn = await Send(HttpMethod.Post, OwnerDeviceKeyApiRoutes.SignIn, new OwnerDeviceSignInRequest(
                    androidChallenge.ChallengeId, androidDeviceId, Convert.ToBase64String(
                        invitedAndroidKey.SignData(DecodeBase64Url(androidChallenge.Nonce), HashAlgorithmName.SHA256,
                            DSASignatureFormat.Rfc3279DerSequence))));
                Check(signIn.StatusCode == HttpStatusCode.OK,
                    "an Android DER signature signs in over HTTP: " + (int)signIn.StatusCode + " " + await signIn.Content.ReadAsStringAsync());
                var androidLogin = (await signIn.Content.ReadFromJsonAsync<LoginResponse>(Json))!;
                var androidToken = androidLogin.Tokens.AccessToken;
                Check(new JwtSecurityTokenHandler().ReadJwtToken(androidToken).Claims.Single(x => x.Type == "amr").Value == "owner-device-key",
                    "an Android owner-device session is issued with amr=owner-device-key");
                Check(androidLogin.User.Id == system.User.Id && androidLogin.Workspace.Id == system.Workspace.Id,
                    "paired devices share the owner's canonical user and workspace");
                using var replay = await Send(HttpMethod.Post, OwnerDeviceKeyApiRoutes.AcceptInvitation,
                    new OwnerDeviceAcceptInvitationRequest(invitation.Token, "replay", "android", AndroidPublicKeySpki, "3"));
                Check(replay.StatusCode == HttpStatusCode.Unauthorized, "an invitation cannot enroll a second device");
                ownerDevices.Revoke(ownerPrincipal, androidDeviceId);
                using var revokedAccess = await Send(HttpMethod.Get, AuthApiRoutes.Me, token: androidToken);
                Check(revokedAccess.StatusCode == HttpStatusCode.Unauthorized, "revoked owner device loses its existing access token");
                using var revokedRefresh = await Send(HttpMethod.Post, AuthApiRoutes.Refresh, new RefreshTokenRequest(androidLogin.Tokens.RefreshToken));
                Check(revokedRefresh.StatusCode == HttpStatusCode.Unauthorized, "revoked owner device cannot refresh its session");
            }
            // Known-answer test on the raw JVM bytes: this exact signature must verify in the DER format
            // the service uses, and must not be mistaken for the IEEE P1363 default.
            using (var vector = ECDsa.Create())
            {
                vector.ImportSubjectPublicKeyInfo(Convert.FromBase64String(AndroidPublicKeySpki), out _);
                var vectorNonce = Convert.FromBase64String(AndroidVectorNonceBase64);
                var vectorSignature = Convert.FromBase64String(AndroidVectorSignatureBase64);
                Check(vector.VerifyData(vectorNonce, vectorSignature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence),
                    "a JVM-produced owner-device signature verifies as RFC 3279 DER");
                Check(!vector.VerifyData(vectorNonce, vectorSignature, HashAlgorithmName.SHA256),
                    "the same JVM signature is not the IEEE P1363 default the owner-device service must avoid");
            }
            var passwordPrincipal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", system.User.Id.ToString()), new Claim("device_id", ownerDeviceId.ToString()),
                new Claim("amr", "system"), new Claim("role", "controller"),
            ], "test"));
            Check(!ownerDevices.IsOwner(passwordPrincipal),
                "ordinary password session cannot inherit owner-device elevation");
            Check(registered.Id == ownerDeviceId, "owner-device registration preserves the associated device id");
        }
        Check((await Read(token)) is { Alias: null, SystemLoginEnabled: true, Revision: 0 }, "default system login enabled");
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.Login, new { username = "nanami", password = OsPassword, clientPlatform = "windows", deviceName = "x", clientVersion = "1" }))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "old username wire contract rejected");
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.LoginAlias, new CreateAliasRequest("developer", AliasPassword, "wrong", 0), token))
            Check(response.StatusCode == HttpStatusCode.Forbidden, "create requires OS reauthentication");
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.LoginAlias, new CreateAliasRequest("existing", AliasPassword, OsPassword, 0), token))
            Check(response.StatusCode == HttpStatusCode.Conflict, "OS account collision even before OS account first login");
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.LoginAlias, new CreateAliasRequest("developer", AliasPassword, OsPassword, 0), token))
            Check(response.StatusCode == HttpStatusCode.Created, "alias creation");
        var configured = await Read(token);
        var alias = await Login("developer", AliasPassword);
        Check(alias.User.Id == system.User.Id && alias.Workspace.Id == system.Workspace.Id && alias.User.Username == "HOST\\nanami", "alias shares canonical user and workspace");
        Check(alias.Session.AuthenticationMethod == "alias", "method metadata");
        using (var response = await Send(HttpMethod.Put, AuthApiRoutes.SystemLogin, new SetSystemLoginRequest(false, OsPassword, configured.Revision), token))
            Check(response.StatusCode == HttpStatusCode.Forbidden, "disable only accepts current alias password");
        using (var response = await Send(HttpMethod.Put, AuthApiRoutes.SystemLogin, new SetSystemLoginRequest(false, AliasPassword, configured.Revision), token))
            Check(response.StatusCode == HttpStatusCode.OK, "disable atomic transition");
        var verifyCalls = provider.VerifyCalls;
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.Login, new LoginRequest("nanami", OsPassword, ClientPlatformKind.Windows, "x", "1")))
            Check(response.StatusCode == HttpStatusCode.Unauthorized && provider.VerifyCalls == verifyCalls, "disabled system login never calls OS password verifier");
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.Refresh, new RefreshTokenRequest(system.Tokens.RefreshToken)))
            Check(response.StatusCode == HttpStatusCode.OK, "system session can refresh after direct system login disabled");
        configured = await Read(token);
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.DeleteAlias, new DeleteAliasRequest("wrong", configured.Revision), token))
            Check(response.StatusCode == HttpStatusCode.Forbidden, "cannot delete last login method with wrong OS password");
        using (var response = await Send(HttpMethod.Put, AuthApiRoutes.LoginAlias, new RenameAliasRequest("developer2", new("alias", AliasPassword), configured.Revision), token))
            Check(response.StatusCode == HttpStatusCode.OK, "rename alias");
        Check((await Read(token)).Alias == "developer2", "rename preserves old session");
        using (var response = await Send(HttpMethod.Put, AuthApiRoutes.SystemLogin, new SetSystemLoginRequest(true, OsPassword, configured.Revision), token))
            Check(response.StatusCode == HttpStatusCode.Conflict, "stale revision cannot overwrite rename");
        configured = await Read(token);
        using (var response = await Send(HttpMethod.Put, AuthApiRoutes.AliasPassword, new ChangeAliasPasswordRequest("another unique password 2026", new("alias", AliasPassword), configured.Revision), token))
            Check(response.StatusCode == HttpStatusCode.NoContent, "password change");
        using (var response = await Send(HttpMethod.Get, AuthApiRoutes.LoginAlias, token: token)) Check(response.StatusCode == HttpStatusCode.Unauthorized, "old access token revoked immediately");
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.Refresh, new RefreshTokenRequest(alias.Tokens.RefreshToken))) Check(response.StatusCode == HttpStatusCode.Unauthorized, "old alias refresh revoked");
        var fresh = await Login("developer2", "another unique password 2026");
        using (var scope = app.Services.CreateScope())
        {
            var user = scope.ServiceProvider.GetRequiredService<IUserRepository>().FindById(system.User.Id)!;
            Check(user.SecurityVersion == 1, "security version persists after password change");
            var oldPrincipal = new ClaimsPrincipal(new ClaimsIdentity(parsed.Claims, "test"));
            Check(!new SessionValidityService(app.Services.GetRequiredService<IServiceScopeFactory>()).IsValid(oldPrincipal), "fresh validator rejects old JWT after restart");
            var capability = app.Services.GetRequiredService<JwtTokenService>().IssueFileCapability(user.Id, system.Workspace.Id, system.Device.Id, "test.app", ["read"], user.SecurityVersion);
            using var response = await Send(HttpMethod.Get, AuthApiRoutes.LoginAlias, token: capability.AccessToken);
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "file capability cannot manage alias");
        }
        provider.Collision = true;
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.Login, new LoginRequest("developer2", "another unique password 2026", ClientPlatformKind.Windows, "x", "1")))
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "external OS alias collision fails closed");
        provider.Collision = false;
        configured = await Read(fresh.Tokens.AccessToken);
        using (var response = await Send(HttpMethod.Post, AuthApiRoutes.DeleteAlias, new DeleteAliasRequest(OsPassword, configured.Revision), fresh.Tokens.AccessToken))
            Check(response.StatusCode == HttpStatusCode.NoContent, "delete restores system login and revokes");
        var restored = await Login("nanami", OsPassword);
        Check(restored.User.Id == system.User.Id && restored.Workspace.Id == system.Workspace.Id, "delete keeps all canonical ownership");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>();
            Check(db.Users.Count() == 1 && db.Workspaces.Count() == 1, "no alias user or workspace created");
            Check(db.LoginCredentials.Single().PasswordHash is null, "deletion clears stored hash");
            var audit = JsonSerializer.Serialize(db.AuthenticationSecurityEvents.ToList());
            Check(!audit.Contains(AliasPassword) && !audit.Contains(OsPassword) && !audit.Contains("developer"), "audit excludes alias and credentials");
        }
        await app.StopAsync();
        Console.WriteLine($"Alias login verification: {checks} checks passed. Native PAM, domain authentication and Windows Service remain separate integration checks.");
    }

    private static void VerifyPasswords()
    {
        var service = new AliasPasswordService(); var credential = new AliasCredential();
        Check(AliasPasswordService.ValidAlias("developer") && !AliasPasswordService.ValidAlias("Developer") && !AliasPasswordService.ValidAlias(" developer")
            && !AliasPasswordService.ValidAlias("root") && !AliasPasswordService.ValidAlias("dev@domain"), "strict alias grammar and reserved names");
        Check(!AliasPasswordService.ValidNewPassword("1234567") && AliasPasswordService.ValidNewPassword("abcdefgh")
            && !AliasPasswordService.ValidNewPassword("123456789012345") && AliasPasswordService.ValidNewPassword(" 空格と Unicode password "), "password scalar policy");
        var first = service.Hash(credential, AliasPassword); var second = service.Hash(credential, AliasPassword);
        Check(first != second, "random salt per hash");
        credential.PasswordHash = first;
        Check(service.Verify(credential, AliasPassword) != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed, "hash verification");
        credential.PasswordHash = "corrupt";
        Check(service.Verify(credential, AliasPassword) == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed, "corrupt hash fails safely");
    }
    private static void VerifyMigration(string root, FakeIdentityProvider provider)
    {
        using var db = new RelaxKonOSDbContext(new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "alias-legacy.db")).Options);
        db.Database.EnsureCreated();
        var userId = Guid.NewGuid(); var workspaceId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, Username = "nanami", Platform = HostPlatformKind.Linux, PlatformIdentity = "HOST\\nanami", CreatedAt = DateTimeOffset.UtcNow });
        db.Workspaces.Add(new Workspace { Id = workspaceId, UserId = userId, Name = "Existing workspace", CreatedAt = DateTimeOffset.UtcNow }); db.SaveChanges();
        var report = IdentityMigrationRunner.Preflight(db.Database.GetDbConnection(), provider);
        Check(report.Single().Problem is null && report.Single().WorkspaceId == workspaceId, "read-only legacy preflight");
        IdentityMigrationRunner.Migrate(db, provider); db.ChangeTracker.Clear();
        Check(db.Users.Single().Id == userId && db.Users.Single().PlatformIdentity == "S-1-5-21-100-100-100-1001" && db.Workspaces.Single().Id == workspaceId, "migration preserves GUID and workspace");
        IdentityMigrationRunner.Migrate(db, provider);
        db.Database.ExecuteSqlRaw("DROP INDEX IX_users_Platform_PlatformIdentity");
        db.Users.Add(new User { Id = Guid.NewGuid(), Username = "HOST\\nanami", Platform = HostPlatformKind.Windows, PlatformIdentity = "S-1-5-21-100-100-100-1001" }); db.SaveChanges();
        Check(IdentityMigrationRunner.Preflight(db.Database.GetDbConnection(), provider).All(x => x.Problem == "duplicate-canonical-identity"), "duplicate identities block preflight without merging");
    }
    private static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("Alias check failed: " + description); checks++; }

    private static byte[] DecodeBase64Url(string value) => Convert.FromBase64String(
        value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + (4 - value.Length % 4) % 4, '='));

    private sealed class FakeIdentityProvider : IIdentityProvider
    {
        public int VerifyCalls;
        public bool Collision;
        private static readonly PlatformUserInfo User = new("S-1-5-21-100-100-100-1001", "HOST\\nanami", HostPlatformKind.Windows, "Nanami", "C:\\Users\\nanami");
        public CredentialVerifyResult Verify(string username, string password)
        { VerifyCalls++; return Lookup(username).Identity is { } info && info.Uid == User.Uid && password == OsPassword ? CredentialVerifyResult.Ok(info) : CredentialVerifyResult.Failed("Rejected", CredentialError.BadCredentials); }
        public PlatformUserInfo GetUserInfo(string username) => Lookup(username).Identity ?? throw new KeyNotFoundException();
        public IdentityLookup Lookup(string identifier) => identifier is "nanami" or "HOST\\nanami" ? new(IdentityLookupStatus.Found, User)
            : identifier == "existing" || Collision && identifier == "developer2" ? new(IdentityLookupStatus.Found, User with { Uid = "S-1-5-21-100-100-100-1002", Username = "HOST\\existing" }) : new(IdentityLookupStatus.NotFound);
        public IdentityLookup LookupIdentity(string identity) => identity == User.Uid ? new(IdentityLookupStatus.Found, User) : new(IdentityLookupStatus.NotFound);
        public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity) => new(true);
    }
}
