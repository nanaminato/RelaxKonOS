using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

public partial class CloudConnectionView
{
    private CancellationTokenSource? _authorization;
    private Uri? _authorizationUri;

    private void ManualAuthorization_Changed(object? sender, RoutedEventArgs e) => UpdateConnectionFields();

    private async void ConfigureProvider_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _client is null) return;
        const string protocol = "baidu-netdisk";
        if (string.IsNullOrWhiteSpace(ProviderClientId.Text) || string.IsNullOrWhiteSpace(ProviderClientSecret.Text))
        { Status.Text = Text("auth_configuration_required"); return; }
        var request = new CloudAuthorizationConfigurationRequest(ProviderClientId.Text.Trim(), ProviderClientSecret.Text);
        ProviderClientSecret.Text = null;
        await RunAsync(async ct =>
        {
            await _client.ConfigureCloudAuthorizationAsync(protocol, request, ct);
            ct.ThrowIfCancellationRequested();
            ProviderConfiguration.IsExpanded = false;
            Status.Text = Text("auth_configured");
        });
    }

    private async void Authorize_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _client is null || _authorization is not null) return;
        var name = ConnectionName.Text?.Trim();
        AuthorizationPanel.IsVisible = false;
        if (string.IsNullOrEmpty(name)) { Status.Text = Text("auth_name_required"); return; }
        const string protocol = "baidu-netdisk";
        var client = _client;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Guid? sessionId = null;
        _authorization = source;
        _busy = true;
        UpdateButtons();
        AuthorizationPanel.IsVisible = true;
        AuthorizationStatus.Text = Text("auth_starting");
        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
            providerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var providers = await client.CloudAuthorizationProvidersAsync(providerTimeout.Token);
            source.Token.ThrowIfCancellationRequested();
            if (!providers.Any(p => p.Protocol == protocol && p.Configured))
            {
                AuthorizationStatus.Text = Text("auth_not_configured");
                return;
            }
            // Finish a bounded start even if the view closes, so the resulting session can be cancelled.
            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var session = await client.StartCloudAuthorizationAsync(new(protocol, name, EnableWrites.IsChecked == true, _editing), startTimeout.Token);
            if (source.IsCancellationRequested)
            {
                await CancelSessionQuietlyAsync(client, session.Id);
                return;
            }
            sessionId = session.Id;
            if (!CloudAuthorizationNavigation.TryGetBrowserUri(protocol, session.AuthorizationUrl, out _authorizationUri))
                throw new InvalidOperationException("Unsupported authorization address");
            AuthorizationCode.Text = session.UserCode ?? "";
            AuthorizationCode.IsVisible = !string.IsNullOrEmpty(session.UserCode);
            await OpenAuthorizationBrowserAsync();
            source.Token.ThrowIfCancellationRequested();
            while (session.Status == "pending")
            {
                if (DateTimeOffset.UtcNow >= session.ExpiresAt)
                {
                    AuthorizationStatus.Text = Text("auth_expired");
                    return;
                }
                AuthorizationStatus.Text = Text("auth_pending") + " " + session.ExpiresAt.ToLocalTime().ToString("t");
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(session.PollIntervalSeconds, 1, 60)), source.Token);
                using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                session = await client.CloudAuthorizationAsync(session.Id, pollTimeout.Token);
                source.Token.ThrowIfCancellationRequested();
            }
            AuthorizationStatus.Text = Text("auth_" + (session.Status is "completed" or "denied" or "expired" or "cancelled" ? session.Status : "failed"));
            if (session.Status == "completed" && session.ConnectionId is { } id)
            {
                sessionId = null;
                Saved?.Invoke(id);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (OperationCanceledException) { AuthorizationStatus.Text = Text("timeout"); }
        catch (Exception)
        {
            // Provider responses and redirect URLs can contain credentials; never show/log raw exceptions.
            if (!source.IsCancellationRequested) AuthorizationStatus.Text = Text("auth_failed");
        }
        finally
        {
            if (ReferenceEquals(_authorization, source))
            {
                _authorizationUri = null;
                AuthorizationCode.Text = "";
                AuthorizationCode.IsVisible = false;
                _authorization = null;
                _busy = false;
                UpdateButtons();
            }
            if (sessionId is { } pendingId) await CancelSessionQuietlyAsync(client, pendingId);
        }
    }

    private async Task OpenAuthorizationBrowserAsync()
    {
        if (_authorizationUri is { } uri && TopLevel.GetTopLevel(this) is { } top)
            if (!await top.Launcher.LaunchUriAsync(uri)) Status.Text = Text("auth_browser_failed");
    }

    private async void AuthorizationOpen_Click(object? sender, RoutedEventArgs e)
    {
        try { await OpenAuthorizationBrowserAsync(); }
        catch (Exception) { AuthorizationStatus.Text = Text("auth_browser_failed"); }
    }

    private void AuthorizationCancel_Click(object? sender, RoutedEventArgs e)
    {
        CancelAuthorization();
        AuthorizationStatus.Text = Text("auth_cancelled");
    }

    private void CancelAuthorization()
    {
        _authorization?.Cancel();
        _authorization = null;
        _busy = false;
        _authorizationUri = null;
        AuthorizationCode.Text = "";
        AuthorizationPanel.IsVisible = false;
        UpdateButtons();
    }

    private static async Task CancelSessionQuietlyAsync(ExplorerClient client, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await client.CancelCloudAuthorizationAsync(id, timeout.Token); }
        catch (Exception) { /* Server expiry bounds a session when cancellation cannot reach it. */ }
    }
}
