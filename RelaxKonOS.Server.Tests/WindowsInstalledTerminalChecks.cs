using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using RelaxKonOS.Protocol.Hubs;

internal static class WindowsInstalledTerminalChecks
{
    internal static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return password.ToString();
            if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
            else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
        }
    }
    internal static async Task RunAsync(string url, string username, string password)
    {
        // The installed test host uses a self-signed TLS certificate. Credentials stay in memory.
        using var http = new HttpClient(new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var response = await http.PostAsJsonAsync(url.TrimEnd('/') + "/api/v1.0/auth/login", new
        {
            identifier = username, password, clientPlatform = "windows",
            deviceName = "Terminal repair verification", clientVersion = "0.1.0"
        });
        response.EnsureSuccessStatusCode();
        using var login = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = login.RootElement.GetProperty("tokens").GetProperty("accessToken").GetString()!;
        HubConnection Connect() => new HubConnectionBuilder().WithUrl(url.TrimEnd('/') + "/hubs/terminals", options =>
        {
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            options.HttpMessageHandlerFactory = _ => new HttpClientHandler
            { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            options.WebSocketConfiguration = socket => socket.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }).Build();
        await using var hub = Connect();
        var marker = "terminal_" + Guid.NewGuid().ToString("N");
        var output = new StringBuilder();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        hub.On<byte[]>("OnOutput", bytes =>
        {
            lock (gate)
            {
                output.Append(Encoding.UTF8.GetString(bytes));
                if (output.ToString().Contains(marker)) ready.TrySetResult();
            }
        });
        await hub.StartAsync();
        var started = await hub.InvokeAsync<AttachTerminalResponse>("Start", new StartTerminalRequest(80, 24, 0, 0, null, null), null);
        try
        {
            var half = marker.Length / 2;
            var input = $"whoami; Write-Output $env:USERPROFILE; Write-Output ('RK_ADMIN=' + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)); Write-Output ('{marker[..half]}' + '{marker[half..]}')\r";
            await hub.InvokeAsync("Input", Encoding.UTF8.GetBytes(input));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            lock (gate)
            {
                if (!output.ToString().Contains("\\" + username.Split('\\').Last(), StringComparison.OrdinalIgnoreCase)
                    || output.ToString().Contains("nt authority\\system", StringComparison.OrdinalIgnoreCase)
                    || !output.ToString().Contains("RK_ADMIN=False", StringComparison.Ordinal))
                    throw new Exception("Terminal whoami did not confirm the authenticated OS account.");
            }
            await hub.InvokeAsync("Resize", 120, 40, 0, 0);
            // A second session must start while the first owns a Helper pipe connection.
            await using var second = Connect();
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.On<int>("OnProcessExited", _ => exited.TrySetResult());
            await second.StartAsync();
            var other = await second.InvokeAsync<AttachTerminalResponse>("Start", new StartTerminalRequest(80, 24, 0, 0, null, null), null);
            await second.InvokeAsync("Input", Encoding.UTF8.GetBytes("exit\r"));
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await hub.StopAsync();
            await hub.StartAsync();
            var attached = await hub.InvokeAsync<AttachTerminalResponse>("AttachExisting", started.SessionId);
            if (attached.SessionId != started.SessionId) throw new Exception("Terminal restoration failed.");
            Console.WriteLine("Installed Windows terminal authenticated identity, restricted privileges, input/output, resize, natural exit, concurrent sessions and reconnect passed.");
            await using var administrator = Connect();
            var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var adminOutput = new StringBuilder();
            var success = "admin_" + Guid.NewGuid().ToString("N");
            administrator.On<byte[]>("OnOutput", bytes =>
            {
                adminOutput.Append(Encoding.UTF8.GetString(bytes));
                if (adminOutput.ToString().Contains(success)) verified.TrySetResult();
            });
            await administrator.StartAsync();
            var elevated = await administrator.InvokeAsync<AttachTerminalResponse>("StartAdministrator",
                new StartTerminalRequest(80, 24, 0, 0, null, null), null);
            try
            {
                var userName = "rkterm_" + Guid.NewGuid().ToString("N")[..8];
                var part = success.Length / 2;
                var script = "$ErrorActionPreference='Stop'; $created=$false; $tested=$false; try { "
                    + "Write-Output ('RK_ADMIN=' + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)); "
                    + $"New-LocalUser -Name '{userName}' -Password (ConvertTo-SecureString ('Aa9!'+[guid]::NewGuid().ToString('N')) -AsPlainText -Force) | Out-Null; $created=$true; "
                    + $"$tested=[bool](Get-LocalUser -Name '{userName}').SID "
                    + $"}} finally {{ if ($created) {{ Remove-LocalUser -Name '{userName}' }} }}; "
                    + $"if ($tested -and !(Get-LocalUser -Name '{userName}' -ErrorAction SilentlyContinue)) {{ Write-Output ('{success[..part]}' + '{success[part..]}') }}\r";
                await administrator.InvokeAsync("Input", Encoding.UTF8.GetBytes(script));
                await verified.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (!adminOutput.ToString().Contains("RK_ADMIN=True")) throw new Exception("Administrator terminal is not elevated.");
                var list = await administrator.InvokeAsync<List<TerminalSessionInfo>>("ListSessions");
                if (!list.Any(session => session.SessionId == elevated.SessionId && session.IsAdministrator))
                    throw new Exception("Administrator session metadata was lost.");
                await administrator.StopAsync();
                await administrator.StartAsync();
                await administrator.InvokeAsync<AttachTerminalResponse>("AttachExisting", elevated.SessionId);
                Console.WriteLine("Administrator terminal elevated identity, New-LocalUser/Get-LocalUser, cleanup and reconnect passed.");
            }
            finally { await administrator.InvokeAsync("CloseSession", elevated.SessionId); }
        }
        finally { await hub.InvokeAsync("CloseSession", started.SessionId); }
    }
}
