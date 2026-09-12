exec(open('alias_implementation.py',encoding='utf-8').read().split("edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs'")[0])
edit('RelaxKonOS.Server/Program.cs','builder.Services.AddSingleton<IIdentityProvider, WindowsLogonProvider>();','builder.Services.AddSingleton<IIdentityProvider>(_ => new BoundedIdentityProvider(new WindowsLogonProvider()));')
edit('RelaxKonOS.Server/Program.cs','builder.Services.AddSingleton<IIdentityProvider, LinuxPamProvider>();','builder.Services.AddSingleton<IIdentityProvider>(_ => new BoundedIdentityProvider(new LinuxPamProvider()));')
edit('RelaxKonOS.Server/Program.cs','builder.Services.AddSingleton<AuthSessionStore>();','builder.Services.AddSingleton<AuthSessionStore>();\nbuilder.Services.AddHostedService<AuthenticationRetentionService>();')
edit('RelaxKonOS.Server/Program.cs','    db.Database.EnsureCreated();','''    var identityPreflight = IdentityMigrationRunner.Preflight(db.Database.GetDbConnection(), scope.ServiceProvider.GetRequiredService<IIdentityProvider>());
    if (identityPreflight.Any(entry => entry.Problem is not null))
        throw new InvalidOperationException("Identity preflight failed. Run auth preflight --database <absolute path> and resolve ownership before upgrading.");
    db.Database.EnsureCreated();''')
edit('RelaxKonOS.Server/Program.cs','    var language = context.Request.GetTypedHeaders()', '''    if (context.Request.Path.Value?.Contains("/auth/", StringComparison.OrdinalIgnoreCase) == true)
    {
        context.Response.Headers.CacheControl = "no-store";
        var bodyLimit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 8192;
    }
    var language = context.Request.GetTypedHeaders()''')
edit('RelaxKonOS.Server/Identity/AuthSecurityOptions.cs','    public int AccountFailureRetentionHours','    public int SecurityEventRetentionDays { get; set; } = 30;\n    public int MaximumSecurityEvents { get; set; } = 100_000;\n    public int AccountFailureRetentionHours')
edit('RelaxKonOS.Server/Storage/AliasCredentialRepository.cs','            db.AuthenticationSecurityEvents.Add(audit);','''            db.AuthenticationSecurityEvents.Add(audit);
            if (audit.EventType == "AccountLoginRecovered")
                db.AccountFailureStates.Where(x => x.AccountKey == "user:" + user.Id.ToString("D")).ExecuteDelete();''')
edit('RelaxKonOS.Server/Identity/AuthMaintenanceCommand.cs','            await db.AccountFailureStates.Where(x => x.AccountKey == id.ToString("D")).ExecuteDeleteAsync();','')
edit('RelaxKonOS.Server/Identity/AuthMaintenanceCommand.cs','// Commit and cooldown clearing share one outer transaction through the recovery operation below.','// Repository clears this canonical cooldown in the same transaction as recovery and audit.')
# Canonical IDs are passed separately from arbitrary anonymous input, so random GUID identifiers cannot fill persistent buckets.
p='RelaxKonOS.Server/Identity/LoginProtectionService.cs'
edit(p,'CancellationToken ct)\n    {','CancellationToken ct, Guid? canonicalUserId = null)\n    {')
edit(p,'var key = AccountKey(username);','var key = canonicalUserId is { } id ? "user:" + id.ToString("D") : AccountKey(username);')
edit(p,'        if (Guid.TryParseExact(identifier, "D", out var id)) return id.ToString("D");\n','')
edit(p,'var accountIp = AccountIpFailures.TryGetValue(key + "|" + ipKey,','var accountIp = AccountIpFailures.TryGetValue(BucketKey(AccountIpFailures, key + "|" + ipKey),')
edit(p,'IpFailures.Count >= MaximumBuckets && !IpFailures.ContainsKey(ipKey) ? "overflow" : ipKey','BucketKey(IpFailures, ipKey)')
edit(p,'AccountIpFailures.Count >= MaximumBuckets ? "overflow" : key + "|" + ipKey','BucketKey(AccountIpFailures, key + "|" + ipKey)')
edit(p,'IpFailures.Count >= MaximumBuckets ? "overflow" : ipKey','BucketKey(IpFailures, ipKey)')
edit(p,'    private static void Cleanup()','''    private static string BucketKey(ConcurrentDictionary<string, TransientFailureState> buckets, string key)
        => buckets.ContainsKey(key) || buckets.Count < MaximumBuckets ? key : "overflow";
    private static void Cleanup()''')
edit('RelaxKonOS.Server/Identity/LoginAuthenticationService.cs','if (user is not null) await CheckAsync(key, ip, ct);','if (user is not null) await CheckAsync(key, ip, ct, user.Id);')
edit('RelaxKonOS.Server/Identity/LoginAuthenticationService.cs','await protection.RecordFailureAsync(key, ip, ct);','await protection.RecordFailureAsync(key, ip, ct, user?.Id);')
edit('RelaxKonOS.Server/Identity/LoginAuthenticationService.cs','private async Task CheckAsync(string key, IPAddress? ip, CancellationToken ct)','private async Task CheckAsync(string key, IPAddress? ip, CancellationToken ct, Guid? canonicalUserId = null)')
edit('RelaxKonOS.Server/Identity/LoginAuthenticationService.cs','protection.CheckAsync(key, ip, ct)','protection.CheckAsync(key, ip, ct, canonicalUserId)')
edit('RelaxKonOS.Server/Identity/LoginAuthenticationService.cs','            if (!verified.Success ||','            if (verified.Error == CredentialError.Unknown) throw new AliasAuthenticationException(503, "authentication-unavailable");\n            if (!verified.Success ||')
edit('RelaxKonOS.Server/Identity/AliasCredentialService.cs','protection.CheckAsync(key, http.Connection.RemoteIpAddress, ct)','protection.CheckAsync(key, http.Connection.RemoteIpAddress, ct, user.Id)')
edit('RelaxKonOS.Server/Identity/AliasCredentialService.cs','protection.RecordFailureAsync(key, http.Connection.RemoteIpAddress, ct)','protection.RecordFailureAsync(key, http.Connection.RemoteIpAddress, ct, user.Id)')
edit('RelaxKonOS.Server/Endpoints/AuthEndpoints.cs','protection.RecordSuccessAsync(login.ProtectionKey, http.Connection.RemoteIpAddress, ct)','protection.RecordSuccessAsync(login.ProtectionKey, http.Connection.RemoteIpAddress, ct, user.Id)')
edit('Client/RelaxKonOS.Client/Apps/Settings/Views/AliasOperationDialogView.axaml','Watermark=','PlaceholderText=')
edit('Client/RelaxKonOS.Client/Services/Diagnostics/NetworkDiagnosticsHandler.cs','request.RequestUri?.AbsolutePath ?? request.Method.Method','NetworkDiagnosticsService.SafeUrl(request.RequestUri)')
edit('Client/RelaxKonOS.Client/Services/Diagnostics/NetworkDiagnosticsService.cs','    internal void Record(NetworkDiagnosticEntry entry)\n    {','''    internal void Record(NetworkDiagnosticEntry entry)
    {
        if (entry.PathAndQuery.Contains("/auth/", StringComparison.OrdinalIgnoreCase)) return;
        entry = entry with { PathAndQuery = SafeUrl(new Uri(entry.PathAndQuery, UriKind.RelativeOrAbsolute)),
            RequestUrl = entry.RequestUrl is null ? null : SafeUrl(new Uri(entry.RequestUrl, UriKind.RelativeOrAbsolute)) };''')
# Preserve existing localization formatting: apply only changed values and append new keys to HEAD text.
import json,subprocess
for lang in ['en-US','zh-CN','ja-JP']:
 for filename in ['settings.json','login.json']:
  p=Path('Client/RelaxKonOS.Client/Localization')/lang/filename; current=json.loads(p.read_text()); original=subprocess.check_output(['git','show','HEAD:'+p.as_posix()]).decode('utf-8-sig'); old=json.loads(original)
  for key,value in old.items():
   if current.get(key)!=value: original=original.replace(json.dumps(key,ensure_ascii=False)+': '+json.dumps(value,ensure_ascii=False),json.dumps(key,ensure_ascii=False)+': '+json.dumps(current[key],ensure_ascii=False))
  additions={k:v for k,v in current.items() if k not in old}
  if additions:
   pos=original.rfind('}'); body=original[:pos].rstrip(); original=body+',\n'+',\n'.join('    '+json.dumps(k,ensure_ascii=False)+': '+json.dumps(v,ensure_ascii=False) for k,v in additions.items())+'\n}\n'
  p.write_text(original,encoding='utf-8')
