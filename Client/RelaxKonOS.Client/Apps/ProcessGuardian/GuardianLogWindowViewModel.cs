using System.Collections.ObjectModel;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Client.Apps.ProcessGuardian;

/// <summary>Owns a non-modal, automatically reconnecting live log viewer for one workload.</summary>
public sealed partial class GuardianLogWindowViewModel(IAuthSession session, GuardianWorkloadDto workload) : ObservableObject, IAsyncDisposable
{
    private HubConnection? _connection;
    public ObservableCollection<string> Lines { get; } = [];
    public string Title => LocalizedText.Format("guardian.logs.title", workload.Name);
    [ObservableProperty] private string _statusText = LocalizedText.Get("guardian.logs.connecting");

    public async Task StartAsync()
    {
        if (session.ServerUrl is null || session.Tokens is null)
        {
            StatusText = LocalizedText.Get("guardian.logs.disconnected");
            return;
        }

        var hubUrl = new Uri(new Uri(session.ServerUrl), RelaxKonOSEndpoints.GuardianLogsHubPath.TrimStart('/')).ToString();
        var connection = _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => session.GetAccessTokenAsync(TimeSpan.FromMinutes(1)))
            .WithAutomaticReconnect()
            .Build();
        connection.On<IReadOnlyList<GuardianLogEntryDto>>(GuardianLogsHubEvents.OnLogSnapshot, ReplaceLogs);
        connection.Reconnected += async _ => await SubscribeAsync(connection);
        connection.Closed += error =>
        {
            Dispatcher.UIThread.Post(() => StatusText = error is null
                ? LocalizedText.Get("guardian.logs.disconnected")
                : LocalizedText.Format("guardian.logs.failed", error.Message));
            return Task.CompletedTask;
        };

        try
        {
            await connection.StartAsync();
            await SubscribeAsync(connection);
            StatusText = LocalizedText.Get("guardian.logs.live");
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("guardian.logs.failed", exception.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null) return;
        try
        {
            if (connection.State == HubConnectionState.Connected)
                await connection.InvokeAsync(GuardianLogsHubMethods.Unsubscribe, workload.Id);
        }
        catch { /* Closing a viewer must not depend on network availability. */ }
        await connection.DisposeAsync();
    }

    private async Task SubscribeAsync(HubConnection connection)
    {
        var logs = await connection.InvokeAsync<IReadOnlyList<GuardianLogEntryDto>>(GuardianLogsHubMethods.Subscribe, workload.Id);
        ReplaceLogs(logs);
    }

    private void ReplaceLogs(IReadOnlyList<GuardianLogEntryDto> logs) => Dispatcher.UIThread.Post(() =>
    {
        Lines.Clear();
        foreach (var log in logs)
            Lines.Add($"{log.Timestamp.LocalDateTime:G} [{log.Stream}] {log.Message}");
    });
}
