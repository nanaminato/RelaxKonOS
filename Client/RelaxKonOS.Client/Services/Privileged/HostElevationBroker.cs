using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Services.Privileged;

/// <summary>
/// The single client-side gateway for a short-lived host-administrator grant.  It owns the
/// password prompt and one safe retry after a server explicitly rejects a mutation for a
/// missing grant; application view models must not implement their own password retry loops.
/// </summary>
public interface IHostElevationBroker
{
    Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
        CancellationToken cancellationToken = default);
}

public sealed class HostElevationBroker(HttpClient http, IAuthSession session, IWindowManager windows) : IHostElevationBroker
{
    public async Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        try { return await operation(); }
        catch (Exception exception) when (IsGrantRequired(exception))
        {
            if (!await EnsureGrantAsync(capability, target, cancellationToken)) throw;
            return await operation();
        }
    }

    private async Task<bool> EnsureGrantAsync(HostElevationCapability capability, string target, CancellationToken cancellationToken)
    {
        try { return (await GrantAsync(capability, target, null, null, cancellationToken)).Elevated; }
        catch (RelaxKonOSAuthException exception) when (HasProblem(exception, "elevation-password-required"))
        {
            return await RequestPasswordAsync((account, password) => GrantAsync(capability, target, password, account, cancellationToken));
        }
    }

    private async Task<HostElevationResult> GrantAsync(HostElevationCapability capability, string target, string? password,
        string? administratorUsername,
        CancellationToken cancellationToken)
    {
        if (session.State != AuthSessionState.Authenticated || session.EffectiveBaseUrl is null)
            throw new RelaxKonOSAuthException(new ProblemDetails("https://relaxkonos.app/problems/elevation-session-unavailable", "Elevation", 401, null, null));
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new RelaxKonOSAuthException(new ProblemDetails("https://relaxkonos.app/problems/elevation-session-unavailable", "Elevation", 401, null, null));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(session.EffectiveBaseUrl), PrivilegedApiRoutes.Elevation.TrimStart('/')))
        {
            Content = JsonContent.Create(new HostElevationRequest(capability, target, password, administratorUsername), options: RelaxKonOSJsonOptions.Default),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
        };
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ProblemDetails? problem = null;
            try { problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default, cancellationToken); }
            catch (System.Text.Json.JsonException) { }
            throw new RelaxKonOSAuthException(problem ?? new ProblemDetails("https://relaxkonos.app/problems/elevation-request-failed", "Elevation", (int)response.StatusCode, null, null));
        }
        return await response.Content.ReadFromJsonAsync<HostElevationResult>(RelaxKonOSJsonOptions.Default, cancellationToken)
            ?? throw new RelaxKonOSAuthException(new ProblemDetails("https://relaxkonos.app/problems/elevation-empty-response", "Elevation", 502, null, null));
    }

    private async Task<bool> RequestPasswordAsync(Func<string, string, Task<HostElevationResult>> authorize)
    {
        return await windows.ShowSystemDialogAsync<bool>(LocalizedText.Get("installation.elevation_title"), dialog =>
        {
            var account = new TextBox
            {
                Text = session.CurrentServer?.Platform == HostPlatformKind.Linux ? "root" : session.CurrentUser?.Username,
                PlaceholderText = LocalizedText.Get("explorer.operations.elevation_account"),
            };
            var password = new TextBox { PasswordChar = '•', PlaceholderText = LocalizedText.Get("settings.host_time.password") };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            RelaxKonOS.UI.Themes.ThemeResources.Bind(error, TextBlock.ForegroundProperty, "DangerBrush");
            var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
            var confirm = new Button { Content = LocalizedText.Get("common.ok"), Classes = { "primary" } };
            cancel.Click += (_, _) => { password.Text = string.Empty; dialog.Cancel(); };
            confirm.Click += async (_, _) =>
            {
                var secret = password.Text ?? string.Empty;
                password.Text = string.Empty;
                if (string.IsNullOrWhiteSpace(secret)) { error.Text = LocalizedText.Get("settings.host_time.password_required"); password.Focus(); return; }
                confirm.IsEnabled = cancel.IsEnabled = false;
                try
                {
                    if ((await authorize(account.Text?.Trim() ?? string.Empty, secret)).Elevated) { dialog.Close(true); return; }
                    error.Text = LocalizedText.Get("settings.host_time.password_invalid");
                }
                catch (RelaxKonOSAuthException exception) when (HasProblem(exception, "elevation-password-invalid"))
                {
                    error.Text = LocalizedText.Get("settings.host_time.password_invalid");
                }
                catch (RelaxKonOSAuthException exception) when (HasProblem(exception, "elevation-account-not-administrator"))
                {
                    error.Text = LocalizedText.Get("settings.host_time.administrator_required");
                }
                catch { error.Text = LocalizedText.Get("settings.host_time.password_check_failed"); }
                finally { confirm.IsEnabled = cancel.IsEnabled = true; password.Focus(); }
            };
            return new StackPanel
            {
                Margin = new Thickness(20), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = LocalizedText.Get("installation.elevation_message"), TextWrapping = TextWrapping.Wrap },
                    account, password, error,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, confirm } },
                },
            };
        }, new RelaxKonOS.Core.Primitives.Size(460, 260));
    }

    private static bool IsGrantRequired(Exception exception) => exception switch
    {
        RelaxKonOSAuthException error => error.Status == (int)HttpStatusCode.Forbidden && HasProblem(error, "elevation-required"),
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } error => string.Equals(error.Message, "webserver.elevation_required", StringComparison.Ordinal),
        _ => false,
    };
    private static bool HasProblem(RelaxKonOSAuthException exception, string suffix) => exception.Type.EndsWith('/' + suffix, StringComparison.Ordinal);
}
