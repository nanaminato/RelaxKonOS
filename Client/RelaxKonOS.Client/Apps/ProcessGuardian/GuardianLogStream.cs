using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Client.Apps.ProcessGuardian;

internal static class GuardianLogStream
{
    public static async IAsyncEnumerable<IReadOnlyList<GuardianLogEntryDto>> WatchAsync(
        IAuthSession session, string id, Func<bool> isCurrent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (session.EffectiveBaseUrl is not { } baseUrl || session.Tokens is null || !isCurrent())
            yield break;

        var url = new Uri(new Uri(baseUrl), RelaxKonOSEndpoints.GuardianLogsHubPath.TrimStart('/'));
        bool ConnectionIsCurrent() => isCurrent() && session.EffectiveBaseUrl == baseUrl;
        bool CanReceive() => !cancellationToken.IsCancellationRequested && ConnectionIsCurrent();
        var snapshots = Channel.CreateBounded<IReadOnlyList<GuardianLogEntryDto>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        await using var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                ServerCertificateTrust.ConfigureSignalR(options, url);
                options.AccessTokenProvider = async () =>
                {
                    if (!CanReceive()) return null;
                    var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1));
                    return CanReceive() ? token : null;
                };
            })
            .WithAutomaticReconnect()
            .Build();

        void Publish(IReadOnlyList<GuardianLogEntryDto> snapshot)
        {
            if (CanReceive()) snapshots.Writer.TryWrite(snapshot);
        }

        async Task SubscribeAsync()
        {
            if (!CanReceive()) return;
            Publish(await connection.InvokeAsync<IReadOnlyList<GuardianLogEntryDto>>(
                GuardianLogsHubMethods.Subscribe, id, cancellationToken));
        }

        using var subscription = connection.On<IReadOnlyList<GuardianLogEntryDto>>(GuardianLogsHubEvents.OnLogSnapshot, Publish);
        connection.Reconnected += async _ =>
        {
            try { await SubscribeAsync(); }
            catch (Exception error) { snapshots.Writer.TryComplete(error); }
        };
        connection.Closed += error =>
        {
            snapshots.Writer.TryComplete(error);
            return Task.CompletedTask;
        };

        void SessionChanged(object? sender, AuthSessionStateChangedEventArgs args)
        {
            if (!ConnectionIsCurrent()) snapshots.Writer.TryComplete();
        }

        session.StateChanged += SessionChanged;
        try
        {
            if (!CanReceive()) yield break;
            await connection.StartAsync(cancellationToken);
            await SubscribeAsync();
            await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken))
            {
                if (!CanReceive()) yield break;
                yield return snapshot;
            }
        }
        finally { session.StateChanged -= SessionChanged; }
    }
}
