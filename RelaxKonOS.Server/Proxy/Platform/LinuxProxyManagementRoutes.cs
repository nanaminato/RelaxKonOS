using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Proxy.Platform;

/// <summary>Reads the route selected by Linux policy for each protected destination.</summary>
internal static class LinuxProxyManagementRoutes
{
    private static string? Executable => File.Exists("/usr/sbin/ip") ? "/usr/sbin/ip"
        : File.Exists("/usr/bin/ip") ? "/usr/bin/ip" : null;

    internal static bool Available => Executable is not null;

    internal static async Task<ProxyLinuxRouteIdentity[]?> ReadAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.ManagementAddresses.Count > 1) return null;
        var routes = new List<ProxyLinuxRouteIdentity>();
        foreach (var address in snapshot.ManagementAddresses.Prepend(snapshot.DefaultGateway).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!IPAddress.TryParse(address, out var destination)) return null;
            if (destination.IsIPv4MappedToIPv6) destination = destination.MapToIPv4();
            var route = await LookupAsync(destination, ct);
            if (route is null) return null;
            routes.Add(route);
        }

        return routes[0].Interface == snapshot.EgressInterface && routes[0].Type == "unicast" && routes[0].Gateway is null
            ? routes.ToArray() : null;
    }

    private static async Task<ProxyLinuxRouteIdentity?> LookupAsync(IPAddress destination, CancellationToken ct)
    {
        var output = await RunIpAsync(["-j", destination.AddressFamily == AddressFamily.InterNetwork ? "-4" : "-6",
            "route", "get", destination.ToString()], ct);
        if (output is null) return null;
        try
        {
            using var json = JsonDocument.Parse(output);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != 1) return null;
            var entry = json.RootElement[0];
            var network = Property(entry, "dev");
            var type = Property(entry, "type") ?? "unicast";
            if (string.IsNullOrWhiteSpace(network) || type is not ("unicast" or "local")) return null;
            // ip may include or omit the local table without changing delivery to this host.
            var table = type == "local" && network == "lo" ? null : Property(entry, "table");
            return new(destination.ToString(), network, Property(entry, "gateway"),
                Property(entry, "prefsrc") ?? Property(entry, "src"), table, type);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    internal static async Task<string?> RunIpAsync(string[] arguments, CancellationToken ct)
    {
        var executable = Executable;
        if (executable is null) return null;
        using var command = new Process { StartInfo = new(executable) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        TrustedProcessEnvironment.Apply(command.StartInfo);
        foreach (var argument in arguments)
            command.StartInfo.ArgumentList.Add(argument);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        Task? errors = null;
        try
        {
            if (!command.Start()) return null;
            errors = command.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var buffer = new char[4097];
            var read = await command.StandardOutput.ReadBlockAsync(buffer.AsMemory(), deadline.Token);
            if (read == buffer.Length) return null;
            await Task.WhenAll(errors, command.WaitForExitAsync(deadline.Token));
            return command.ExitCode == 0 ? new string(buffer, 0, read) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { return null; }
        finally
        {
            try { if (!command.HasExited) command.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            if (errors is not null)
            {
                try { await errors.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
            }
        }
    }

    private static string? Property(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) ? value.ToString() : null;
}
