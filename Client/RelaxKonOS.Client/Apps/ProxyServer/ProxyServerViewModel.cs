using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.Client.Apps.ProxyServer;

/// <summary>One authorized-inbound checkbox in the credential editor.</summary>
public sealed partial class InboundGrant(ProxyServerInbound inbound) : ObservableObject
{
    public ProxyServerInbound Inbound { get; } = inbound;
    public string Name => Inbound.Name;
    public LocalizedStatus ProtocolLabel => LocalizedStatus.Key("proxy_server.protocol." + Inbound.Protocol);
    public string Endpoint => $"{Inbound.ListenAddress}:{Inbound.ListenPort}";
    [ObservableProperty] private bool isSelected;
}

/// <summary>One client format offered by the export or subscription page.</summary>
public sealed partial class FormatChoice(string format, LocalizedStatus label) : ObservableObject
{
    public string Format { get; } = format;
    public LocalizedStatus Label { get; } = label;
    public bool Supported { get; init; } = true;
    public LocalizedStatus Reason { get; init; } = LocalizedStatus.Literal("");
    [ObservableProperty] private bool isSelected;
}

/// <summary>One inbound protocol preset in the create/edit form.</summary>
public sealed record ProtocolChoice(ProxyServerProtocol Protocol)
{
    public LocalizedStatus Label => LocalizedStatus.Key("proxy_server.protocol." + Protocol);
}

public partial class ProxyServerViewModel(IProxyServerClient client) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private ProxyServerSnapshot? _snapshot;
    private bool _invalidated;
    private bool _loading;
    private bool _exportOptionsLoaded;
    private bool _subscriptionFormatsBuilt;

    private static readonly string[] DerivedProperties =
    [
        nameof(CanEdit), nameof(IsSupported), nameof(IsInstalled), nameof(IsRunning), nameof(IsRecoveryRequired),
        nameof(ShowInstall), nameof(ShowUninstall), nameof(ShowRuntimeActions), nameof(ShowRecovery), nameof(ShowRollback),
        nameof(HasExport), nameof(HasIssuedToken), nameof(HasPrincipals), nameof(HasCredentials), nameof(HasSubscriptions),
        nameof(HasInbounds), nameof(Protocol), nameof(ShowCertificateField), nameof(ShowRealityFields), nameof(ShowWebSocketField),
        nameof(ServiceStateText), nameof(BootText), nameof(AppliedText), nameof(VersionText), nameof(RevisionText), nameof(StatisticsText),
        nameof(UriEnabled), nameof(SingBoxEnabled), nameof(MihomoEnabled), nameof(AllFormatsEnabled), nameof(ExportReasons)
    ];

    private void NotifyDerived() { foreach (var name in DerivedProperties) OnPropertyChanged(name); }

    public Func<LocalizedStatus, LocalizedStatus, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] private bool canRead;
    [ObservableProperty] private bool canManage;
    [ObservableProperty] private bool canExport;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasUnsavedChanges;
    [ObservableProperty] private LocalizedStatus statusText = LocalizedStatus.Literal("");
    [ObservableProperty] private LocalizedStatus outcomeText = LocalizedStatus.Literal("");
    [ObservableProperty] private LocalizedStatus operationText = LocalizedStatus.Literal("");
    [ObservableProperty] private LocalizedStatus exportWarning = LocalizedStatus.Literal("");

    [ObservableProperty] private ProxyServerInbound? selectedInbound;
    [ObservableProperty] private ProxyServerPrincipal? selectedPrincipal;
    [ObservableProperty] private ProxyServerCredential? selectedCredential;
    [ObservableProperty] private ProxyServerSubscription? selectedSubscription;

    [ObservableProperty] private int protocolIndex;
    [ObservableProperty] private string inboundName = "";
    [ObservableProperty] private string listenAddress = "0.0.0.0";
    [ObservableProperty] private string listenPort = "24443";
    [ObservableProperty] private string publicHost = "";
    [ObservableProperty] private string publicPort = "24443";
    [ObservableProperty] private string serverName = "";
    [ObservableProperty] private string certificateId = "";
    [ObservableProperty] private string handshakeHost = "";
    [ObservableProperty] private string handshakePort = "443";
    [ObservableProperty] private string webSocketPath = "/proxy";
    [ObservableProperty] private bool inboundEnabled = true;

    [ObservableProperty] private string principalName = "";
    [ObservableProperty] private string principalUserId = "";

    [ObservableProperty] private string credentialName = "";
    [ObservableProperty] private string credentialExpiresAt = "";
    [ObservableProperty] private bool credentialEnabled = true;

    [ObservableProperty] private string subscriptionBaseUrl = "";
    [ObservableProperty] private string issuedToken = "";
    [ObservableProperty] private string issuedUrls = "";
    [ObservableProperty] private string exportText = "";
    [ObservableProperty] private string previewText = "";
    [ObservableProperty] private Bitmap? qrCode;

    public ObservableCollection<ProxyServerInbound> Inbounds { get; } = [];
    public ObservableCollection<ProxyServerPrincipal> Principals { get; } = [];
    public ObservableCollection<ProxyServerCredential> Credentials { get; } = [];
    public ObservableCollection<ProxyServerSubscription> Subscriptions { get; } = [];
    public ObservableCollection<InboundGrant> CredentialGrants { get; } = [];
    public ObservableCollection<FormatChoice> SubscriptionFormats { get; } = [];
    public ObservableCollection<ProtocolChoice> ProtocolChoices { get; } =
        new(Enum.GetValues<ProxyServerProtocol>().Select(protocol => new ProtocolChoice(protocol)));
    public ObservableCollection<FormatChoice> ExportFormats { get; } = [];

    public ProxyServerSnapshot? Snapshot => _snapshot;
    public bool CanEdit => CanManage && !IsBusy && !_invalidated;
    public bool IsSupported => _snapshot?.Capabilities.Supported == true;
    public bool IsInstalled => _snapshot?.Status.Installed == true;
    public bool IsRunning => _snapshot?.Status.Running == true;
    public bool IsRecoveryRequired => _snapshot?.Status.RecoveryRequired == true;
    public bool ShowInstall => CanManage && IsSupported && !IsInstalled;
    public bool ShowUninstall => CanManage && IsInstalled;
    public bool ShowRuntimeActions => CanManage && IsInstalled && !IsRecoveryRequired;
    public bool ShowRecovery => CanManage && IsRecoveryRequired;
    public bool ShowRollback => CanManage && IsInstalled && _snapshot?.PreviousRevision is not null;
    public bool HasExport => !string.IsNullOrEmpty(ExportText);
    public bool UriEnabled => ExportFormats.FirstOrDefault(f => f.Format == ProxyServerExportFormats.Uri)?.Supported == true;
    public bool SingBoxEnabled => ExportFormats.FirstOrDefault(f => f.Format == ProxyServerExportFormats.SingBox)?.Supported == true;
    public bool MihomoEnabled => ExportFormats.FirstOrDefault(f => f.Format == ProxyServerExportFormats.Mihomo)?.Supported == true;
    public bool AllFormatsEnabled => UriEnabled && SingBoxEnabled && MihomoEnabled;
    public LocalizedStatus ExportReasons => LocalizedStatus.Join("\n", ExportFormats
        .Where(format => !format.Supported && !format.Reason.IsEmpty).Select(format => format.Reason));
    public bool HasIssuedToken => !string.IsNullOrEmpty(IssuedToken);
    public bool HasPrincipals => Principals.Count > 0;
    public bool HasCredentials => Credentials.Count > 0;
    public bool HasSubscriptions => Subscriptions.Count > 0;
    public bool HasInbounds => Inbounds.Count > 0;
    public ProxyServerProtocol Protocol => ProtocolChoices[Math.Clamp(ProtocolIndex, 0, ProtocolChoices.Count - 1)].Protocol;
    public bool ShowCertificateField => Protocol != ProxyServerProtocol.VlessReality;
    public bool ShowRealityFields => Protocol == ProxyServerProtocol.VlessReality;
    public bool ShowWebSocketField => Protocol == ProxyServerProtocol.VlessWebSocket;
    public LocalizedStatus ServiceStateText => _snapshot is null ? LocalizedStatus.Key("proxy_server.state.unavailable")
        : !_snapshot.Capabilities.Supported ? LocalizedStatus.Key("proxy_server.state.unsupported")
        : _snapshot.Status.RecoveryRequired ? LocalizedStatus.Key("proxy_server.state.recovery")
        : !_snapshot.Status.Installed ? LocalizedStatus.Key("proxy_server.state.not_installed")
        : _snapshot.Status.Running ? LocalizedStatus.Key("proxy_server.state.running")
        : LocalizedStatus.Key("proxy_server.state.stopped");
    public LocalizedStatus BootText => _snapshot?.Status.BootEnabled switch
    {
        true => LocalizedStatus.Key("proxy_server.state.boot_enabled"),
        false => LocalizedStatus.Key("proxy_server.state.boot_disabled"),
        _ => LocalizedStatus.Key("proxy_server.state.unknown")
    };
    public LocalizedStatus AppliedText => _snapshot?.Status.Applied == true
        ? LocalizedStatus.Key("proxy_server.state.applied") : LocalizedStatus.Key("proxy_server.state.not_applied");
    public LocalizedStatus VersionText => LocalizedStatus.Literal(_snapshot?.Status.Version ?? SingBoxManifest.Version);
    public LocalizedStatus RevisionText => LocalizedStatus.Format("proxy_server.revisions",
        _snapshot?.Configuration.Revision ?? 0, _snapshot?.PublishedRevision?.ToString() ?? "—", _snapshot?.PreviousRevision?.ToString() ?? "—");
    public LocalizedStatus StatisticsText => _snapshot?.Capabilities.Statistics == true
        ? LocalizedStatus.Key("proxy_server.state.statistics_available") : LocalizedStatus.Key("proxy_server.state.statistics_unavailable");

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is nameof(InboundName) or nameof(ListenAddress) or nameof(ListenPort) or nameof(PublicHost)
            or nameof(PublicPort) or nameof(ServerName) or nameof(CertificateId) or nameof(HandshakeHost) or nameof(HandshakePort)
            or nameof(WebSocketPath) or nameof(InboundEnabled) or nameof(ProtocolIndex) or nameof(PrincipalName)
            or nameof(PrincipalUserId) or nameof(CredentialName) or nameof(CredentialExpiresAt) or nameof(CredentialEnabled)
            or nameof(SubscriptionBaseUrl))
            HasUnsavedChanges = true;
        if (e.PropertyName == nameof(ProtocolIndex))
        {
            NotifyDerived();
        }
    }

    partial void OnSelectedInboundChanged(ProxyServerInbound? value) => LoadInboundEditor(value);
    partial void OnSelectedPrincipalChanged(ProxyServerPrincipal? value)
    {
        if (value is null) return;
        _loading = true; PrincipalName = value.DisplayName; PrincipalUserId = value.RelaxKonOSUserId ?? ""; _loading = false;
    }
    partial void OnSelectedCredentialChanged(ProxyServerCredential? value) => LoadCredentialEditor(value);

    private void LoadInboundEditor(ProxyServerInbound? value)
    {
        ClearExport();
        if (value is null) return;
        _loading = true;
        ProtocolIndex = Math.Max(0, ProtocolChoices.ToList().FindIndex(choice => choice.Protocol == value.Protocol));
        InboundName = value.Name; ListenAddress = value.ListenAddress; ListenPort = value.ListenPort.ToString();
        PublicHost = value.PublicHost; PublicPort = value.PublicPort.ToString(); ServerName = value.ServerName;
        CertificateId = value.CertificateId?.ToString("D") ?? "";
        HandshakeHost = value.HandshakeHost; HandshakePort = value.HandshakePort.ToString();
        WebSocketPath = value.WebSocketPath; InboundEnabled = value.Enabled;
        _loading = false;
    }

    private void LoadCredentialEditor(ProxyServerCredential? value)
    {
        ClearExport();
        _exportOptionsLoaded = false;
        CredentialGrants.Clear();
        foreach (var inbound in Inbounds)
            CredentialGrants.Add(new InboundGrant(inbound) { IsSelected = value?.InboundIds.Contains(inbound.Id) == true });
        if (value is null) return;
        _loading = true;
        CredentialName = value.Name; CredentialEnabled = value.Enabled;
        CredentialExpiresAt = value.ExpiresAt?.ToString("O") ?? "";
        SelectedPrincipal = Principals.FirstOrDefault(p => p.Id == value.PrincipalId) ?? SelectedPrincipal;
        _loading = false;
    }

    [RelayCommand] private Task RefreshAsync() => RunAsync(LoadAsync);

    private async Task LoadAsync()
    {
        var snapshot = await client.ReadAsync(_lifetime.Token);
        var inboundId = SelectedInbound?.Id; var credentialId = SelectedCredential?.Id;
        var principalId = SelectedPrincipal?.Id; var subscriptionId = SelectedSubscription?.Id;
        _snapshot = snapshot;
        _loading = true;
        Inbounds.Clear(); foreach (var inbound in snapshot.Configuration.Inbounds) Inbounds.Add(inbound);
        Principals.Clear(); foreach (var principal in snapshot.Principals) Principals.Add(principal);
        Credentials.Clear(); foreach (var credential in snapshot.Credentials) Credentials.Add(credential);
        Subscriptions.Clear(); foreach (var subscription in snapshot.Subscriptions) Subscriptions.Add(subscription);
        SubscriptionBaseUrl = snapshot.Configuration.SubscriptionBaseUrl ?? "";
        SelectedPrincipal = Principals.FirstOrDefault(p => p.Id == principalId) ?? Principals.FirstOrDefault();
        SelectedInbound = Inbounds.FirstOrDefault(i => i.Id == inboundId);
        SelectedCredential = Credentials.FirstOrDefault(c => c.Id == credentialId);
        SelectedSubscription = Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
        if (!_subscriptionFormatsBuilt) { BuildSubscriptionFormats(); _subscriptionFormatsBuilt = true; }
        _loading = false;
        StatusText = LocalizedStatus.Join(" · ", [
            ServiceStateText,
            LocalizedStatus.Format("proxy_server.status_version", snapshot.Status.Version ?? SingBoxManifest.Version),
            RevisionText, AppliedText]);
        if (snapshot.Status.ProblemCode is { } problem && OutcomeText.IsEmpty) OutcomeText = ProblemText(problem);
        NotifyDerived();
        if (SelectedCredential is not null && CanExport && !_exportOptionsLoaded) await UpdateExportFormatsAsync();
    }

    private void BuildSubscriptionFormats()
    {
        SubscriptionFormats.Clear();
        foreach (var format in ProxyServerExportFormats.All)
            SubscriptionFormats.Add(new FormatChoice(format, FormatLabel(format)) { IsSelected = true });
    }

    [RelayCommand] private void NewInbound()
    {
        SelectedInbound = null;
        _loading = true;
        ProtocolIndex = 0; InboundName = LocalizedText.Get("proxy_server.new_inbound_name");
        ListenAddress = "0.0.0.0"; ListenPort = "24443"; PublicHost = ""; PublicPort = "24443";
        ServerName = ""; CertificateId = ""; HandshakeHost = ""; HandshakePort = "443"; WebSocketPath = "/proxy"; InboundEnabled = true;
        _loading = false;
        HasUnsavedChanges = true;
    }

    [RelayCommand] private Task SaveInboundAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (_snapshot is null) return;
        if (!int.TryParse(ListenPort, out var listenPort) || !int.TryParse(PublicPort, out var publicPort)
            || !int.TryParse(HandshakePort, out var handshakePort)) throw new InvalidOperationException("proxy_server.port_invalid");
        if (ShowCertificateField && !Guid.TryParse(CertificateId, out _)) throw new InvalidOperationException("proxy_server.certificate_invalid");
        var inbound = new ProxyServerInbound(SelectedInbound?.Id ?? Guid.NewGuid(), InboundName.Trim(), Protocol,
            ListenAddress.Trim(), listenPort, PublicHost.Trim(), publicPort, ServerName.Trim(),
            ShowCertificateField ? Guid.Parse(CertificateId) : null,
            ShowRealityFields ? HandshakeHost.Trim() : "", ShowRealityFields ? handshakePort : 443,
            ShowWebSocketField ? WebSocketPath.Trim() : "/proxy", InboundEnabled);
        var all = _snapshot.Configuration.Inbounds.Where(i => i.Id != inbound.Id).Append(inbound).ToArray();
        var validation = SingBoxConfiguration.Validate(new(_snapshot.Configuration.Revision, all, NullIfEmpty(SubscriptionBaseUrl)));
        if (!validation.Valid) throw new InvalidOperationException(validation.Problems[0]);
        await SubmitAsync(HttpMethod.Put, ProxyServerRoutes.Configuration,
            new SaveProxyServerConfiguration(_snapshot.Configuration.Revision, all, NullIfEmpty(SubscriptionBaseUrl)), "proxy_server.saved");
        HasUnsavedChanges = false;
        SelectedInbound = Inbounds.FirstOrDefault(i => i.Id == inbound.Id);
    });

    /// <summary>
    /// A destructive command without a confirm host does nothing. Publishing or deleting must never
    /// proceed silently just because the shell did not wire a dialog.
    /// </summary>
    private Task<bool> ConfirmDestructiveAsync(string titleKey, string messageKey) =>
        ConfirmAsync is { } confirm
            ? confirm(LocalizedStatus.Key(titleKey), LocalizedStatus.Key(messageKey))
            : Task.FromResult(false);

    [RelayCommand] private Task DeleteInboundAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (_snapshot is null || SelectedInbound is null) return;
        if (!await ConfirmDestructiveAsync("proxy_server.delete_inbound", "proxy_server.delete_inbound_warning")) return;
        var all = _snapshot.Configuration.Inbounds.Where(i => i.Id != SelectedInbound.Id).ToArray();
        await SubmitAsync(HttpMethod.Put, ProxyServerRoutes.Configuration,
            new SaveProxyServerConfiguration(_snapshot.Configuration.Revision, all, NullIfEmpty(SubscriptionBaseUrl)), "proxy_server.saved");
        HasUnsavedChanges = false;
        SelectedInbound = null;
    });

    [RelayCommand] private Task SaveBaseUrlAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (_snapshot is null) return;
        await SubmitAsync(HttpMethod.Put, ProxyServerRoutes.Configuration,
            new SaveProxyServerConfiguration(_snapshot.Configuration.Revision, _snapshot.Configuration.Inbounds, NullIfEmpty(SubscriptionBaseUrl)),
            "proxy_server.saved");
        HasUnsavedChanges = false;
    });

    [RelayCommand] private void NewPrincipal()
    {
        SelectedPrincipal = null; _loading = true; PrincipalName = ""; PrincipalUserId = ""; _loading = false;
        HasUnsavedChanges = true;
    }

    [RelayCommand] private Task SavePrincipalAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (string.IsNullOrWhiteSpace(PrincipalName)) throw new InvalidOperationException("proxy_server.principal_invalid");
        await SubmitAsync(SelectedPrincipal is null ? HttpMethod.Post : HttpMethod.Put,
            SelectedPrincipal is null ? ProxyServerRoutes.Principals : WithId(ProxyServerRoutes.PrincipalPattern, SelectedPrincipal.Id),
            new SaveProxyServerPrincipal(SelectedPrincipal?.Id, PrincipalName.Trim(), NullIfEmpty(PrincipalUserId)), "proxy_server.saved");
        HasUnsavedChanges = false;
    });

    [RelayCommand] private Task DeletePrincipalAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (SelectedPrincipal is null) return;
        if (!await ConfirmDestructiveAsync("proxy_server.delete_principal", "proxy_server.delete_principal_warning")) return;
        await SubmitAsync(HttpMethod.Delete, WithId(ProxyServerRoutes.PrincipalPattern, SelectedPrincipal.Id), new { }, "proxy_server.saved");
        HasUnsavedChanges = false;
    });

    [RelayCommand] private void NewCredential()
    {
        SelectedCredential = null;
        _loading = true; CredentialName = ""; CredentialEnabled = true; CredentialExpiresAt = "";
        CredentialGrants.Clear();
        foreach (var inbound in Inbounds) CredentialGrants.Add(new InboundGrant(inbound));
        _loading = false;
        HasUnsavedChanges = true;
    }

    [RelayCommand] private Task SaveCredentialAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (SelectedPrincipal is null) throw new InvalidOperationException("proxy_server.principal_required");
        var grants = CredentialGrants.Where(g => g.IsSelected).Select(g => g.Inbound.Id).ToArray();
        if (grants.Length == 0) throw new InvalidOperationException("proxy_server.grants_required");
        var expires = ParseExpiry(CredentialExpiresAt);
        var request = new ProxyServerCredentialRequest(SelectedCredential?.Id, SelectedPrincipal.Id, CredentialName.Trim(), grants, CredentialEnabled, expires);
        await SubmitAsync(SelectedCredential is null ? HttpMethod.Post : HttpMethod.Put,
            SelectedCredential is null ? ProxyServerRoutes.Credentials : WithId(ProxyServerRoutes.CredentialPattern, SelectedCredential.Id),
            request, "proxy_server.credential_saved");
        HasUnsavedChanges = false;
    });

    [RelayCommand] private Task RotateCredentialAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (SelectedCredential is null) return;
        if (!await ConfirmDestructiveAsync("proxy_server.rotate", "proxy_server.rotate_warning")) return;
        var rotation = new ProxyServerCredentialRotation(CredentialEnabled, ParseExpiry(CredentialExpiresAt), string.IsNullOrWhiteSpace(CredentialExpiresAt));
        await SubmitAsync(HttpMethod.Post, WithId(ProxyServerRoutes.CredentialRotatePattern, SelectedCredential.Id), rotation, "proxy_server.credential_saved");
        ClearExport();
    });

    [RelayCommand] private Task DeleteCredentialAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (SelectedCredential is null) return;
        if (!await ConfirmDestructiveAsync("proxy_server.delete_credential", "proxy_server.delete_credential_warning")) return;
        await SubmitAsync(HttpMethod.Delete, WithId(ProxyServerRoutes.CredentialPattern, SelectedCredential.Id), new { }, "proxy_server.credential_saved");
        HasUnsavedChanges = false;
    });

    [RelayCommand] private Task ValidateAsync() => RunAsync(async () =>
    {
        RequireManage();
        var validation = await client.ValidateAsync(_lifetime.Token);
        PreviewText = validation.Preview ?? "";
        OutcomeText = validation.Valid ? LocalizedStatus.Key("proxy_server.validation_ok")
            : LocalizedStatus.Join("\n", validation.Problems.Select(ProblemText));
    });

    [RelayCommand] private Task ApplyAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (!await ConfirmDestructiveAsync("proxy_server.apply", "proxy_server.apply_warning")) return;
        await SubmitAsync(HttpMethod.Post, ProxyServerRoutes.Apply, new { }, "proxy_server.published");
    });

    [RelayCommand] private Task RollbackAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (!await ConfirmDestructiveAsync("proxy_server.rollback", "proxy_server.rollback_warning")) return;
        await SubmitAsync(HttpMethod.Post, ProxyServerRoutes.Rollback, new { }, "proxy_server.rolled_back");
    });

    [RelayCommand] private Task RecoverAsync() => RunAsync(async () =>
    {
        RequireManage();
        await SubmitAsync(HttpMethod.Post, ProxyServerRoutes.Recovery, new { }, "proxy_server.recovered");
    });

    [RelayCommand] private Task RuntimeAsync(string? action) => RunAsync(async () =>
    {
        RequireManage();
        if (!Enum.TryParse<ProxyServerAction>(action, true, out var parsed)) return;
        if (parsed is ProxyServerAction.Restart && !await ConfirmDestructiveAsync("proxy_server.restart", "proxy_server.restart_warning")) return;
        await SubmitAsync(HttpMethod.Post, ProxyServerRoutes.RuntimeActions + "/" + parsed.ToString().ToLowerInvariant(), new { }, "proxy_server.action_done");
    });

    [RelayCommand] private Task InstallAsync() => RunAsync(async () =>
    {
        RequireManage();
        await SubmitAsync(HttpMethod.Post, ProxyServerRoutes.RuntimeInstall, new { }, "proxy_server.installed");
    });

    [RelayCommand] private Task UpgradeAsync() => RunAsync(async () =>
    {
        RequireManage();
        await SubmitAsync(HttpMethod.Post, ProxyServerRoutes.RuntimeUpgrade, new ProxyServerUpgradeRequest(SingBoxManifest.Version), "proxy_server.installed");
    });

    [RelayCommand] private Task UninstallAsync(string? keepData) => RunAsync(async () =>
    {
        RequireManage();
        var keep = keepData != "delete";
        if (!await ConfirmDestructiveAsync(keep ? "proxy_server.uninstall" : "proxy_server.uninstall_delete",
                keep ? "proxy_server.uninstall_warning" : "proxy_server.uninstall_delete_warning")) return;
        await SubmitAsync(HttpMethod.Post, ProxyServerRoutes.RuntimeUninstall, new ProxyServerUninstallRequest(keep), "proxy_server.uninstalled");
    });

    [RelayCommand] private Task LoadExportFormatsAsync() => RunAsync(UpdateExportFormatsAsync);

    private async Task UpdateExportFormatsAsync()
    {
        if (!CanExport || SelectedCredential is null) return;
        var options = await client.ExportFormatsAsync(SelectedCredential.Id, _lifetime.Token);
        ExportFormats.Clear();
        foreach (var option in options)
            ExportFormats.Add(new FormatChoice(option.Format, FormatLabel(option.Format))
            {
                Supported = option.Supported,
                Reason = option.Reason is null ? LocalizedStatus.Literal("") : ProblemText(option.Reason)
            });
        _exportOptionsLoaded = true;
        NotifyDerived();
    }

    [RelayCommand] private Task ExportAsync(string? format) => RunAsync(async () =>
    {
        if (!CanExport || SelectedCredential is null || string.IsNullOrEmpty(format)) return;
        ClearExport();
        var export = await client.ExportAsync(SelectedCredential.Id, format, _lifetime.Token);
        ExportText = export.Content;
        ExportWarning = export.Warning is null ? LocalizedStatus.Literal("") : ProblemText(export.Warning);
        if (export.Format == ProxyServerExportFormats.Uri && export.Content.Length < 2000 && !export.Content.Contains('\n'))
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(export.Content, QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(data);
            using var stream = new MemoryStream(qr.GetGraphic(8));
            QrCode = new Bitmap(stream);
        }
        NotifyDerived();
    });

    [RelayCommand] private void ClearExport()
    {
        ExportText = "";
        ExportWarning = LocalizedStatus.Literal("");
        QrCode?.Dispose(); QrCode = null;
        NotifyDerived();
    }

    [RelayCommand] private Task IssueSubscriptionAsync() => RunAsync(async () =>
    {
        if (!CanExport || SelectedCredential is null) return;
        var formats = SubscriptionFormats.Where(f => f.IsSelected).Select(f => f.Format).ToArray();
        if (formats.Length == 0) throw new InvalidOperationException("proxy_server.formats_required");
        var issued = await client.IssueSubscriptionAsync(new(SelectedCredential.Id, formats, null), _lifetime.Token);
        IssuedToken = issued.Token;
        IssuedUrls = string.Join('\n', issued.Urls);
        OutcomeText = LocalizedStatus.Key("proxy_server.subscription_issued");
        await LoadAsync();
    });

    [RelayCommand] private Task RevokeSubscriptionAsync() => RunAsync(async () =>
    {
        if (SelectedSubscription is null) return;
        if (!await ConfirmDestructiveAsync("proxy_server.revoke", "proxy_server.revoke_warning")) return;
        await client.RevokeSubscriptionAsync(SelectedSubscription.Id, _lifetime.Token);
        OutcomeText = LocalizedStatus.Key("proxy_server.subscription_revoke_done");
        await LoadAsync();
    });

    private async Task SubmitAsync(HttpMethod method, string route, object body, string successKey)
    {
        var accepted = await client.SubmitAsync(method, route, body, Guid.NewGuid().ToString("N"), _lifetime.Token);
        OperationText = LocalizedStatus.Format("proxy_server.operation_pending", accepted.OperationId.ToString("D"));
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var operation = await client.OperationAsync(accepted.OperationId, _lifetime.Token);
            OperationText = LocalizedStatus.Format("proxy_server.operation_state", operation.OperationId.ToString("D"), operation.State, operation.Stage);
            if (operation.State is ProxyOperationState.Failed)
                throw new InvalidOperationException(string.IsNullOrEmpty(operation.ProblemCode) ? "proxy_server.operation_failed" : operation.ProblemCode);
            if (operation.State is ProxyOperationState.Succeeded) break;
            if (attempt == 179) throw new InvalidOperationException("proxy_server.operation_pending");
            await Task.Delay(1000, _lifetime.Token);
        }
        await LoadAsync();
        OutcomeText = LocalizedStatus.Key(successKey);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || _invalidated) return;
        IsBusy = true; OutcomeText = LocalizedStatus.Literal("");
        NotifyDerived();
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or FormatException)
        { OutcomeText = ProblemText(ex.Message); }
        finally { IsBusy = false; NotifyDerived(); }
    }

    private void RequireManage()
    {
        if (!CanManage) throw new InvalidOperationException("proxy_server.permission_denied");
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? ParseExpiry(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            throw new InvalidOperationException("proxy_server.expiry_invalid");
        return parsed.ToUniversalTime();
    }

    private static string WithId(string pattern, Guid id) => pattern.Replace("{id:guid}", id.ToString("D"), StringComparison.Ordinal);
    private static LocalizedStatus FormatLabel(string format) => LocalizedStatus.Key("proxy_server.format." + format.Replace('-', '_'));

    internal static LocalizedStatus ProblemText(string code) => LocalizedStatus.Key("proxy_server.problem." + code.Replace("proxy_server.", ""), code);

    public void InvalidateSession()
    {
        _invalidated = true; CanManage = CanExport = false; ClearExport();
        IssuedToken = ""; IssuedUrls = "";
        _lifetime.Cancel(); OutcomeText = LocalizedStatus.Key("proxy_server.session_changed");
        NotifyDerived();
    }

    public void Dispose() { InvalidateSession(); _lifetime.Dispose(); QrCode?.Dispose(); }
}
