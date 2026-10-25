using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>Loopback-only smart HTTP Git fixture for installed Helper acceptance. Credentials
/// are synthetic; no host credential manager, external Git service or real token is used.</summary>
internal static class HttpGitAcceptanceServer
{
    internal const string Username = "测试 & user";
    internal const string Password = "acceptance &|<>^%!\"'() token";

    internal static async Task RunAsync(string git, string root, int port, string report, bool secure)
    {
        Directory.CreateDirectory(root);
        using var initialize = Process.Start(new ProcessStartInfo(git, ["init", "--bare", "--initial-branch=main", Path.Combine(root, "remote.git")])
            { UseShellExecute = false, CreateNoWindow = true })!;
        await initialize.WaitForExitAsync();
        if (initialize.ExitCode != 0) throw new Exception("Cannot initialize HTTP fixture.");
        using var listener = new HttpListener();
        listener.Prefixes.Add($"{(secure ? "https" : "http")}://127.0.0.1:{port}/");
        listener.Start();
        var accepted = 0;
        var challenged = 0;
        void Save() => File.WriteAllText(report, JsonSerializer.Serialize(new { ready = true, accepted, challenged }));
        Save();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Username + ":" + Password));
        while (!lifetime.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync().WaitAsync(lifetime.Token);
            try
            {
                if (context.Request.Headers["Authorization"] != expected)
                {
                    challenged++;
                    context.Response.StatusCode = 401;
                    context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"RelaxKonOS acceptance\"";
                    context.Response.ContentLength64 = 0;
                    continue;
                }
                accepted++;
                var start = new ProcessStartInfo(git, ["-c", "http.receivepack=true", "http-backend"])
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.Environment["GIT_PROJECT_ROOT"] = root;
                start.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
                start.Environment["REQUEST_METHOD"] = context.Request.HttpMethod;
                start.Environment["PATH_INFO"] = context.Request.Url!.AbsolutePath;
                start.Environment["QUERY_STRING"] = context.Request.Url.Query.TrimStart('?');
                start.Environment["CONTENT_TYPE"] = context.Request.ContentType ?? "";
                start.Environment["REMOTE_USER"] = "acceptance";
                start.Environment["REMOTE_ADDR"] = "127.0.0.1";
                if (context.Request.Headers["Git-Protocol"] is { } protocol) start.Environment["HTTP_GIT_PROTOCOL"] = protocol;
                using var input = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(input, lifetime.Token);
                start.Environment["CONTENT_LENGTH"] = input.Length.ToString();
                using var backend = Process.Start(start)!;
                using var output = new MemoryStream();
                var outputTask = backend.StandardOutput.BaseStream.CopyToAsync(output, lifetime.Token);
                var errorTask = backend.StandardError.ReadToEndAsync(lifetime.Token);
                input.Position = 0;
                await input.CopyToAsync(backend.StandardInput.BaseStream, lifetime.Token);
                backend.StandardInput.Close();
                await Task.WhenAll(outputTask, backend.WaitForExitAsync(lifetime.Token));
                _ = await errorTask;
                var bytes = output.ToArray();
                var separator = -1;
                for (var index = 0; index + 3 < bytes.Length; index++)
                    if (bytes.AsSpan(index, 4).SequenceEqual("\r\n\r\n"u8)) { separator = index; break; }
                if (separator < 0 || backend.ExitCode != 0) throw new Exception("Invalid Git CGI response.");
                foreach (var line in Encoding.ASCII.GetString(bytes, 0, separator).Split("\r\n"))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var name = line[..colon];
                    var value = line[(colon + 1)..].Trim();
                    if (name.Equals("Status", StringComparison.OrdinalIgnoreCase)) context.Response.StatusCode = int.Parse(value.Split(' ')[0]);
                    else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) context.Response.ContentType = value;
                    else context.Response.Headers[name] = value;
                }
                context.Response.ContentLength64 = bytes.Length - separator - 4;
                await context.Response.OutputStream.WriteAsync(bytes.AsMemory(separator + 4), lifetime.Token);
            }
            finally { context.Response.Close(); Save(); }
        }
    }
}
