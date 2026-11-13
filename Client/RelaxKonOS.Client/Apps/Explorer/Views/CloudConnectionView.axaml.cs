using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

public partial class CloudConnectionView : UserControl
{
    private readonly CancellationTokenSource _lifetime = new();
    private ExplorerClient? _client;
    private Guid? _editing;
    private bool _busy;

    internal event Action? CloseRequested;
    internal event Action<Guid>? Saved;

    public CloudConnectionView() => InitializeComponent();

    internal void Initialize(ExplorerClient client, CloudConnectionDto? source, bool rejected)
    {
        _client = client;
        _editing = source?.Id;
        ConnectionName.Text = source?.Name;
        Protocol.SelectedIndex = CloudProtocolText.FormIndex(source?.Protocol);
        EnableWrites.IsChecked = source?.CanWrite == true;
        Protocol.IsEnabled = source is null;
        UpdateConnectionFields();
        CredentialWarning.IsVisible = rejected;
    }

    internal void ShowCredentialWarning() => CredentialWarning.IsVisible = true;

    internal void Stop()
    {
        CancelAuthorization();
        _lifetime.Cancel();
        _lifetime.Dispose();
        Secret.Text = SessionToken.Text = RefreshToken.Text = ProviderClientSecret.Text = null;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void UpdateButtons()
    {
        FormFields.IsEnabled = SaveButton.IsEnabled = !_busy;
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || _client is null) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = request.Token;
        var lifetime = _lifetime.Token;
        _busy = true;
        UpdateButtons();
        Status.Text = Text("working");
        try
        {
            await operation(ct);
            if (lifetime.IsCancellationRequested) return;
            if (Status.Text == Text("working")) Status.Text = "";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { Status.Text = Text("timeout"); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException
            or UnauthorizedAccessException or System.Text.Json.JsonException or RelaxKonOSAuthException)
        {
            if (!lifetime.IsCancellationRequested) Status.Text = Text("error");
        }
        finally
        {
            _busy = false;
            if (!lifetime.IsCancellationRequested) UpdateButtons();
        }
    }

    private void Protocol_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateConnectionFields();

    /// <summary>
    /// Each provider states exactly the fields it needs. Nothing is inferred from the protocol
    /// string at save time, so an unknown provider keeps the generic endpoint form rather than
    /// silently becoming WebDAV.
    /// </summary>
    private void UpdateConnectionFields()
    {
        if (S3Fields is null || PrivateDav is null || OAuthHelp is null || ManualAuthorization is null
            || AuthorizeButton is null || SaveButton is null || ProviderConfiguration is null) return;
        var index = Protocol.SelectedIndex;
        var oauth = index >= 2;
        var manual = ManualAuthorization.IsChecked == true;
        S3Fields.IsVisible = index == 1;
        PrivateDav.IsVisible = index == 0;
        // An OAuth provider has no endpoint or bucket to fill in, so the whole group goes away
        // rather than leaving an empty frame above the credentials.
        ServerSection.IsVisible = !oauth;
        Endpoint.IsVisible = !oauth;
        ProtocolHint.Text = Text(index switch { 1 => "hint_s3", 2 => "hint_baidu", _ => "hint_webdav" });
        ProviderConfiguration.IsVisible = oauth;
        CredentialSection.IsVisible = !oauth || manual;
        SessionTokenField.IsVisible = index == 1;
        RefreshTokenField.IsVisible = oauth && manual;
        UserNameLabel.Text = Text(oauth ? "oauth_client_id" : "user");
        SecretLabel.Text = Text(oauth ? "oauth_client_secret" : "secret");
        UserName.PlaceholderText = Text(oauth ? "oauth_client_id" : "user");
        Secret.PlaceholderText = Text(oauth ? "oauth_client_secret" : "secret");
        ManualAuthorization.IsVisible = oauth;
        OAuthHelp.IsVisible = oauth && manual;
        AuthorizationSection.IsVisible = oauth;
        AuthorizeButton.IsVisible = oauth && !manual;
        AuthorizeButton.Content = Text(_editing is null ? "auth_connect_new" : "reauthorize");
        SaveButton.IsVisible = !oauth || manual;
        // Authorization owns the primary action in that mode; the footer states where the
        // connection will come from instead of leaving the space blank.
        FooterHint.IsVisible = oauth && !manual && _editing is null;
        FooterHint.Text = Text("auth_footer_hint");
        FormSubtitle.Text = Text(_editing is null ? "form_add_hint" : "form_edit_hint");
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var protocol = Protocol.SelectedIndex switch { 1 => CloudProtocolText.S3, 2 => CloudProtocolText.BaiduNetdisk, _ => CloudProtocolText.WebDav };
        var oauth = Protocol.SelectedIndex >= 2;
        var request = new CloudConnectionRequest(ConnectionName.Text ?? "", protocol, Endpoint.Text ?? "",
            Region.Text, Bucket.Text, Prefix.Text, UserName.Text ?? "", Secret.Text ?? "",
            oauth ? RefreshToken.Text : SessionToken.Text,
            protocol == CloudProtocolText.WebDav && PrivateDav.IsChecked == true, EnableWrites.IsChecked == true);
        await RunAsync(async ct =>
        {
            var saved = await _client!.SaveCloudConnectionAsync(_editing, request, ct);
            ct.ThrowIfCancellationRequested();
            Saved?.Invoke(saved.Id);
        });
    }

    private static string Text(string key) => LocalizedText.Get("explorer.cloud." + key);
}
