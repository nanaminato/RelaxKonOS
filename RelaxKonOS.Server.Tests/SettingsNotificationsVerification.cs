using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Hubs;

internal static class SettingsNotificationsVerification
{
    public static async Task RunAsync(string address, Guid owner, Guid workspaceId, Func<Task<long>> mutate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var first = await Peer.ConnectAsync(address, owner, deadline.Token);
        await using var second = await Peer.ConnectAsync(address, owner, deadline.Token);
        await using var foreign = await Peer.ConnectAsync(address, Guid.NewGuid(), deadline.Token);
        await first.SubscribeAsync(workspaceId, allowed: true);
        await second.SubscribeAsync(workspaceId, allowed: true);
        await foreign.SubscribeAsync(workspaceId, allowed: false);
        var revision = await mutate();
        await Task.WhenAll(first.ExpectRevisionAsync(workspaceId, revision), second.ExpectRevisionAsync(workspaceId, revision));
        // A fresh connection must be invalidated even if the persisted setting has not changed again.
        await using var reconnected = await Peer.ConnectAsync(address, owner, deadline.Token);
        await reconnected.SubscribeAsync(workspaceId, allowed: true);
        await reconnected.ExpectRevisionAsync(workspaceId, revision);
        Console.WriteLine("Settings notifications passed: two connections, cross-user subscription denial, reconnect snapshot invalidation, value-free payload.");
    }

    private sealed class Peer(ClientWebSocket socket, CancellationToken cancellationToken) : IAsyncDisposable
    {
        private string _pending = "";

        public static async Task<Peer> ConnectAsync(string address, Guid userId, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-Test-Subject", userId.ToString());
            var uri = new UriBuilder(new Uri(new Uri(address), RelaxKonOSEndpoints.SettingsChangesHubPath)) { Scheme = "ws" }.Uri;
            await socket.ConnectAsync(uri, cancellationToken);
            var peer = new Peer(socket, cancellationToken);
            await peer.SendAsync(new { protocol = "json", version = 1 });
            var handshake = await peer.ReadAsync();
            if (handshake.TryGetProperty("error", out _)) throw new InvalidOperationException("Hub handshake failed.");
            return peer;
        }

        public async Task SubscribeAsync(Guid workspaceId, bool allowed)
        {
            await SendAsync(new { type = 1, invocationId = "subscribe", target = SettingsChangesMethods.Subscribe, arguments = new[] { workspaceId } });
            while (true)
            {
                var message = await ReadAsync();
                if (!message.TryGetProperty("type", out var type) || type.GetInt32() != 3) continue;
                var rejected = message.TryGetProperty("error", out _);
                if (rejected == allowed) throw new InvalidOperationException("Settings subscription authorization mismatch.");
                return;
            }
        }

        public async Task ExpectRevisionAsync(Guid workspaceId, long revision)
        {
            while (true)
            {
                var message = await ReadAsync();
                if (!message.TryGetProperty("target", out var target) || target.GetString() != SettingsChangesMethods.Changed) continue;
                var payload = message.GetProperty("arguments")[0];
                if (payload.EnumerateObject().Any(property => property.Name is not ("workspaceId" or "revision" or "persistedRevision" or "settingId" or "scope")))
                    throw new InvalidOperationException("Settings notifications must not include preference values.");
                if (payload.GetProperty("workspaceId").GetGuid() != workspaceId)
                    throw new InvalidOperationException("Settings notification crossed Workspace boundary.");
                if (payload.GetProperty("revision").GetInt64() == revision) return;
            }
        }

        private async Task SendAsync(object message)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + '\u001e');
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }

        private async Task<JsonElement> ReadAsync()
        {
            var buffer = new byte[4096];
            while (true)
            {
                var separator = _pending.IndexOf('\u001e');
                if (separator >= 0)
                {
                    using var document = JsonDocument.Parse(_pending[..separator]);
                    _pending = _pending[(separator + 1)..];
                    return document.RootElement.Clone();
                }
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Settings Hub closed unexpectedly.");
                _pending += Encoding.UTF8.GetString(buffer, 0, received.Count);
            }
        }

        public ValueTask DisposeAsync()
        {
            socket.Abort();
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
