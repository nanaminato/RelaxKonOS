using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.ProxyServer;

namespace RelaxKonOS.Client.Apps.ProxyServer;

public partial class ProxyServerViewModel(IProxyServerClient client) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private ProxyServerSnapshot? _snapshot;
    private bool _invalidated;
    public Func<Task<bool>>? ConfirmApplyAsync { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanMutate))] private bool canManage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanMutate))] private bool isBusy;
    [ObservableProperty] private bool canExport;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string operationText = "";
    [ObservableProperty] private ProxyServerInbound? selectedInbound;
    [ObservableProperty] private ProxyServerCredential? selectedCredential;
    [ObservableProperty] private string inboundName = "VLESS REALITY";
    [ObservableProperty] private string listenAddress = "0.0.0.0";
    [ObservableProperty] private string listenPort = "24443";
    [ObservableProperty] private string publicHost = "";
    [ObservableProperty] private string publicPort = "24443";
    [ObservableProperty] private string serverName = "";
    [ObservableProperty] private string handshakeHost = "";
    [ObservableProperty] private bool inboundEnabled = true;
    [ObservableProperty] private string credentialName = "";
    [ObservableProperty] private bool credentialEnabled = true;
    [ObservableProperty] private string expiresAt = "";
    [ObservableProperty] private string exportText = "";
    [ObservableProperty] private Bitmap? qrCode;
    public ObservableCollection<ProxyServerInbound> Inbounds { get; } = [];
    public ObservableCollection<ProxyServerCredential> Credentials { get; } = [];
    public bool CanMutate => CanManage && !IsBusy && !_invalidated;
    public bool HasUnsavedChanges { get; private set; }
    private bool _loading;
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is nameof(InboundName) or nameof(ListenAddress) or nameof(ListenPort) or nameof(PublicHost)
            or nameof(PublicPort) or nameof(ServerName) or nameof(HandshakeHost) or nameof(InboundEnabled)
            or nameof(CredentialName) or nameof(CredentialEnabled) or nameof(ExpiresAt)) HasUnsavedChanges = true;
    }
    partial void OnSelectedInboundChanged(ProxyServerInbound? value)
    {
        ClearExport();
        if (value is null) return;
        _loading = true;
        InboundName = value.Name; ListenAddress = value.ListenAddress; ListenPort = value.ListenPort.ToString();
        PublicHost = value.PublicHost; PublicPort = value.PublicPort.ToString(); ServerName = value.ServerName;
        HandshakeHost = value.HandshakeHost; InboundEnabled = value.Enabled;
        _loading = false;
    }
    partial void OnSelectedCredentialChanged(ProxyServerCredential? value)
    {
        ClearExport();
        if (value is null) return;
        _loading = true;
        CredentialName = value.Name; CredentialEnabled = value.Enabled; ExpiresAt = value.ExpiresAt?.ToString("O") ?? "";
        SelectedInbound = Inbounds.FirstOrDefault(i => i.Id == value.InboundId);
        _loading = false;
    }
    [RelayCommand] private Task RefreshAsync() => RunAsync(async () => { await LoadAsync(); });
    private async Task LoadAsync()
    {
        var snapshot = await client.ReadAsync(_lifetime.Token);
        var inboundId = SelectedInbound?.Id; var credentialId = SelectedCredential?.Id;
        _snapshot = snapshot;
        _loading = true;
        Inbounds.Clear(); foreach (var inbound in snapshot.Configuration.Inbounds) Inbounds.Add(inbound);
        Credentials.Clear(); foreach (var credential in snapshot.Credentials) Credentials.Add(credential);
        SelectedInbound = Inbounds.FirstOrDefault(i => i.Id == inboundId);
        SelectedCredential = Credentials.FirstOrDefault(c => c.Id == credentialId);
        _loading = false;
        var status = snapshot.Status;
        var state = !status.Supported ? "unsupported" : status.RecoveryRequired ? "recovery" : !status.Installed ? "not_installed" : status.Running ? "running" : "stopped";
        StatusText = $"sing-box {status.Version ?? SingBoxManifest.Version} · {L(state)} · {L("revision")} {snapshot.Configuration.Revision}/{snapshot.PublishedRevision?.ToString() ?? "—"} · {(status.Applied ? L("applied") : L("pending"))}";
        if (status.ProblemCode is not null) Message = status.ProblemCode;
    }
    [RelayCommand] private void NewInbound() { SelectedInbound = null; HasUnsavedChanges = true; }
    [RelayCommand] private void NewCredential() { SelectedCredential = null; CredentialName = ""; CredentialEnabled = true; ExpiresAt = ""; ClearExport(); }
    [RelayCommand] private Task SaveInboundAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (_snapshot is null || !int.TryParse(ListenPort, out var port) || !int.TryParse(PublicPort, out var publicPort)) throw new InvalidOperationException("proxy_server.config_invalid");
        var inbound = new ProxyServerInbound(SelectedInbound?.Id ?? Guid.NewGuid(), InboundName, ProxyServerProtocol.VlessReality,
            ListenAddress, port, PublicHost, publicPort, ServerName, HandshakeHost, Enabled: InboundEnabled);
        var all = _snapshot.Configuration.Inbounds.Where(i => i.Id != inbound.Id).Append(inbound).ToArray();
        if (!SingBoxConfiguration.Validate(new(0, all)).Valid) throw new InvalidOperationException("proxy_server.config_invalid");
        await SubmitAsync(ProxyServerRoutes.Configuration, new SaveProxyServerConfiguration(_snapshot.Configuration.Revision, all));
        HasUnsavedChanges = false; SelectedInbound = Inbounds.FirstOrDefault(i => i.Id == inbound.Id);
    });
    [RelayCommand] private Task SaveCredentialAsync() => SaveCredentialCoreAsync(false);
    [RelayCommand] private Task RotateCredentialAsync() => SaveCredentialCoreAsync(true);
    private Task SaveCredentialCoreAsync(bool rotate) => RunAsync(async () =>
    {
        RequireManage();
        if (SelectedInbound is null) throw new InvalidOperationException("proxy_server.inbound_required");
        DateTimeOffset? expires = null;
        if (!string.IsNullOrWhiteSpace(ExpiresAt))
        {
            if (!DateTimeOffset.TryParse(ExpiresAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)) throw new InvalidOperationException("proxy_server.expiry_invalid");
            expires = date.ToUniversalTime();
        }
        await SubmitAsync(ProxyServerRoutes.Credentials, new ProxyServerCredentialRequest(SelectedCredential?.Id, CredentialName, SelectedInbound.Id, CredentialEnabled, expires, rotate));
        HasUnsavedChanges = false;
    });
    [RelayCommand] private Task ApplyAsync() => RunAsync(async () =>
    {
        RequireManage();
        if (ConfirmApplyAsync is not null && !await ConfirmApplyAsync()) return;
        await SubmitAsync(ProxyServerRoutes.Apply, new { });
    });
    [RelayCommand] private Task ValidateAsync() => RunAsync(async () =>
    {
        RequireManage(); var result = await client.ValidateAsync(_lifetime.Token);
        Message = result.Valid ? L("valid") : string.Join("\n", result.Problems);
    });
    [RelayCommand] private Task RuntimeAsync(string? action) => RunAsync(async () =>
    {
        RequireManage();
        if (!Enum.TryParse<ProxyServerAction>(action, out var parsed)) return;
        if (parsed is ProxyServerAction.Restart or ProxyServerAction.Recover && ConfirmApplyAsync is not null && !await ConfirmApplyAsync()) return;
        await SubmitAsync(ProxyServerRoutes.Runtime, new ProxyServerActionRequest(parsed));
    });
    [RelayCommand] private Task ExportAsync(string? format) => RunAsync(async () =>
    {
        if (!CanExport || SelectedCredential is null) return;
        ClearExport();
        var export = await client.ExportAsync(new(SelectedCredential.Id, format ?? "uri"), _lifetime.Token);
        ExportText = export.Content;
        if (export.Format == "uri" && export.Content.Length < 2000)
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(export.Content, QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(data);
            using var stream = new MemoryStream(qr.GetGraphic(8));
            QrCode = new Bitmap(stream);
        }
    });
    [RelayCommand] private void ClearExport() { ExportText = ""; QrCode?.Dispose(); QrCode = null; }
    private async Task SubmitAsync(string route, object body)
    {
        var accepted = await client.SubmitAsync(route, body, Guid.NewGuid().ToString("N"), _lifetime.Token);
        OperationText = accepted.OperationId.ToString();
        while (true)
        {
            var operation = await client.OperationAsync(accepted.OperationId, _lifetime.Token);
            OperationText = $"{accepted.OperationId} · {operation.State} · {operation.Stage}";
            if (operation.State is ProxyOperationState.Failed) throw new InvalidOperationException(operation.ProblemCode);
            if (operation.State is ProxyOperationState.Succeeded) break;
            await Task.Delay(1000, _lifetime.Token);
        }
        await LoadAsync();
        Message = L("saved");
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || _invalidated) return;
        IsBusy = true; Message = "";
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException) { Message = ex.Message; }
        finally { IsBusy = false; }
    }
    private void RequireManage() { if (!CanManage) throw new InvalidOperationException("proxy_server.permission_denied"); }
    public void InvalidateSession() { _invalidated = true; CanManage = CanExport = false; ClearExport(); _lifetime.Cancel(); Message = L("session_changed"); }
    public void Dispose() { InvalidateSession(); _lifetime.Dispose(); }
    private static string L(string key) => LocalizedText.Get("proxy_server." + key);
}
