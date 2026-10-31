extern alias modulehelper;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RelaxKonOS.ModuleHost.Protocol;
using modulehelper::RelaxKonOS.PrivilegedHelper;

internal static class WindowsModuleHostChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var secret = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        var nonce = new string('a', 64);
        async Task<MemoryStream> Encode(ModuleHostPipeFrame frame)
        {
            var stream = new MemoryStream();
            await ModuleHostPipeProtocol.WriteAsync(stream, frame, secret, nonce, ModuleHostPipeProtocol.HelperDirection, default);
            stream.Position = 0;
            return stream;
        }
        using var stream = await Encode(new(1, "stdout", "{\"type\":\"ready\"}"));
        var decoded = await ModuleHostPipeProtocol.ReadAsync(stream, secret, nonce, ModuleHostPipeProtocol.HelperDirection, 1, default);
        check(decoded.Payload == "{\"type\":\"ready\"}", "ModuleHost authenticated pipe preserves the exact control payload");
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or FormatException or EndOfStreamException)
            { check(true, message); return; }
            throw new Exception("Expected Windows ModuleHost protocol rejection: " + message);
        }
        var bytes = stream.ToArray();
        await Reject(() => ModuleHostPipeProtocol.ReadAsync(new MemoryStream(bytes), new byte[48], nonce,
            ModuleHostPipeProtocol.HelperDirection, 1, default), "ModuleHost pipe rejects a forged machine authentication key");
        await Reject(() => ModuleHostPipeProtocol.ReadAsync(new MemoryStream(bytes), secret, new string('b', 64),
            ModuleHostPipeProtocol.HelperDirection, 1, default), "ModuleHost pipe rejects cross-connection replay");
        await Reject(() => ModuleHostPipeProtocol.ReadAsync(new MemoryStream(bytes), secret, nonce,
            ModuleHostPipeProtocol.ClientDirection, 1, default), "ModuleHost pipe rejects reflection between client and Helper directions");
        await Reject(() => ModuleHostPipeProtocol.ReadAsync(new MemoryStream(bytes), secret, nonce,
            ModuleHostPipeProtocol.HelperDirection, 2, default), "ModuleHost pipe rejects duplicate or out-of-order frames");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, ModuleHostPipeProtocol.MaximumFrameBytes + 1);
        await Reject(() => ModuleHostPipeProtocol.ReadAsync(new MemoryStream(header), secret, nonce,
            ModuleHostPipeProtocol.HelperDirection, 1, default), "ModuleHost pipe rejects oversized frames before allocating a payload");
        await Reject(() => ModuleHostPipeProtocol.ReadAsync(new MemoryStream(bytes[..^1]), secret, nonce,
            ModuleHostPipeProtocol.HelperDirection, 1, default), "ModuleHost pipe rejects truncated authenticated frames");
        await Reject(() => Task.FromResult(ModuleHostPipeProtocol.Read<ModuleHostCommand>(Encoding.UTF8.GetBytes("{\"type\":\"health\",\"type\":\"shutdown\"}"))),
            "ModuleHost pipe rejects duplicate JSON command keys");
        await Reject(() => Task.FromResult(ModuleHostPipeProtocol.Read<ModuleHostCommand>(Encoding.UTF8.GetBytes("{\"type\":\"health\",\"command\":\"shell\"}"))),
            "ModuleHost pipe rejects unknown JSON command fields");
        await Reject(() => Task.FromResult(ModuleHostPipeProtocol.Read<ModuleHostLaunchRequest>(Encoding.UTF8.GetBytes("{}"))),
            "ModuleHost pipe rejects missing launch identity and artifact fields");
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP Windows native ACL descriptor checks on a non-Windows host.");
            return;
        }
        CheckAcl(check);
        using var current = WindowsIdentity.GetCurrent();
        if (current.User != WindowsModuleHostSecurity.System)
        {
            await using var host = new WindowsModuleHostPipeServer(new("fixture", Convert.ToBase64String(secret), [], []), _ => { });
            try { host.Start(); check(false, "ModuleHost requires LocalSystem"); }
            catch (UnauthorizedAccessException) { check(true, "Windows ModuleHost launcher refuses an ordinary or elevated interactive process"); }
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckAcl(Action<bool, string> check)
    {
        var module = new SecurityIdentifier("S-1-5-21-111-222-333-1007");
        var other = new SecurityIdentifier("S-1-5-21-111-222-333-1008");
        var read = WindowsModuleHostSecurity.DirectoryAcl(module, FileSystemRights.ReadAndExecute);
        var rules = read.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().ToArray();
        check(read.AreAccessRulesProtected && read.GetOwner(typeof(SecurityIdentifier))?.Equals(WindowsModuleHostSecurity.System) == true
            && rules.Length == 3 && !rules.Any(rule => rule.IdentityReference.Equals(other)),
            "Windows module snapshot ACL has exactly System, Administrators and its own dedicated SID, with inheritance disabled");
        check((rules.Single(rule => rule.IdentityReference.Equals(module)).FileSystemRights & ~FileSystemRights.Synchronize) == FileSystemRights.ReadAndExecute,
            "Windows module snapshot grants its account read/execute only");
        var data = WindowsModuleHostSecurity.DirectoryAcl(module, FileSystemRights.Modify);
        var access = data.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(module)).FileSystemRights;
        check((access & FileSystemRights.Write) != 0 && (access & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0,
            "Windows private data grants write access without ACL or ownership authority");
        var pipe = WindowsPrivilegedPipeSecurity.Build("S-1-5-80-1-2-3-4-5", []);
        check(!pipe.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<PipeAccessRule>()
            .Any(rule => rule.IdentityReference.Equals(module)), "Windows Helper pipe ACL excludes every module account");
    }
}
