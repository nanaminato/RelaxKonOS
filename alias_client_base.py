exec(open('alias_implementation.py',encoding='utf-8').read().split("edit('RelaxKonOS.Server/Identity/LinuxPamProvider.cs'")[0])
p=Path('RelaxKonOS.Server/Program.cs'); s=p.read_text(); needle='using var identityHostLock = storageProvider == "sqlite"\n    ? AcquireIdentityHostLock(Path.Combine(builder.Environment.ContentRootPath, storageOpts.DatabasePath)) : null;\n'; first=s.index(needle); s=s[:first+len(needle)]+s[first+len(needle):].replace(needle,''); p.write_text(s,encoding='utf-8')
# No credential buffers enter diagnostics, including auth calls routed through the developer bridge.
edit('Client/RelaxKonOS.Client/Services/Diagnostics/NetworkDiagnosticsService.cs','        if (uri.IsLoopback && uri.Port == DeveloperModeService.BridgePort)','        if (uri.AbsolutePath.Contains("/auth/", StringComparison.OrdinalIgnoreCase)) return false;\n        if (uri.IsLoopback && uri.Port == DeveloperModeService.BridgePort)')
edit('Client/RelaxKonOS.Client/Services/Diagnostics/NetworkDiagnosticsService.cs','                var value = string.Join(", ", header.Value);','''                var value = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Contains("Cookie", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Contains("Token", StringComparison.OrdinalIgnoreCase)
                    ? "[redacted]" : string.Join(", ", header.Value);''')
edit('Client/RelaxKonOS.Client/Services/Diagnostics/NetworkDiagnosticsService.cs','    internal bool ShouldCapture(Uri? uri)','''    internal static string SafeUrl(Uri? uri)
    {
        if (uri is null) return "";
        var path = uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.ToString().Split('?')[0];
        var media = path.IndexOf("/media/", StringComparison.OrdinalIgnoreCase);
        if (media >= 0) path = path[..(media + 7)] + "[redacted]";
        return path;
    }

    internal bool ShouldCapture(Uri? uri)''')
edit('Client/RelaxKonOS.Client/Services/Diagnostics/NetworkDiagnosticsHandler.cs','request.RequestUri?.PathAndQuery ?? string.Empty','NetworkDiagnosticsService.SafeUrl(request.RequestUri)')
edit('Client/RelaxKonOS.Client/Services/Diagnostics/NetworkDiagnosticsHandler.cs','request.RequestUri?.ToString()','NetworkDiagnosticsService.SafeUrl(request.RequestUri)')
edit('Client/RelaxKonOS.Client/Services/Auth/AuthSession.cs','request.Username','request.Identifier')
edit('Client/RelaxKonOS.Client/Services/Auth/RememberedSessionStore.cs','string.Equals(leftUsername.Trim(), rightUsername.Trim(), StringComparison.OrdinalIgnoreCase)','string.Equals(leftUsername, rightUsername, StringComparison.Ordinal)')
edit('Client/RelaxKonOS.Client/Services/Auth/RememberedSessionStore.cs','Username = profile.Username.Trim()','Username = profile.Username')
p='Client/RelaxKonOS.Client/Services/Auth/RememberedSessionStore.cs'
edit(p,'    Task<RememberedProfileSaveResult> UpsertAsync','    Task<RememberedProfileSaveResult> RemoveAsync(string serverUrl, string identifier, CancellationToken ct = default);\n    Task<RememberedProfileSaveResult> UpsertAsync')
edit(p,'        var payload = Serialize(new SavedLoginProfileCollection(','        return await SaveProfilesAsync(profiles, ct);\n    }\n\n    private async Task<RememberedProfileSaveResult> SaveProfilesAsync(List<SavedLoginProfile> profiles, CancellationToken ct)\n    {\n        var payload = Serialize(new SavedLoginProfileCollection(')
edit(p,'    public async Task ClearAsync','''    public async Task<RememberedProfileSaveResult> RemoveAsync(string serverUrl, string identifier, CancellationToken ct = default)
    {
        var profiles = (await LoadAsync(ct)).ToList();
        profiles.RemoveAll(item => SavedLoginProfile.SameProfile(item.ServerUrl, item.Username, serverUrl, identifier));
        return await SaveProfilesAsync(profiles, ct);
    }

    public async Task ClearAsync''')
p='Client/RelaxKonOS.Client/ViewModels/Login/LoginViewModel.cs'
s=Path(p).read_text().replace('Username','Identifier').replace('_username','_identifier').replace('profile.Identifier','profile.Username'); Path(p).write_text(s,encoding='utf-8')
edit('Client/RelaxKonOS.Client/Views/Login/LoginView.axaml','Username','Identifier')
