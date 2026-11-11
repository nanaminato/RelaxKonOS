using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Server.FileServices;

internal static class SmbReadChecks
{
    internal static async Task RunAsync()
    {
        var json = JsonSerializer.Serialize(new FileServiceStatusDto(FileServiceProtocol.Smb, FileServiceRuntimeState.Running, null, true, true), RelaxKonOSJsonOptions.Default);
        using var document = JsonDocument.Parse(json);
        TestAssert.Assert(document.RootElement.GetProperty("protocol").GetString() == "smb" && document.RootElement.GetProperty("state").GetString() == "running",
            "SMB REST enums must follow the current shared camelCase serialization options.");
        var transport = new Transport();
        var linux = new LinuxSambaPlatformAdapter(new PrivilegedSmbOperations(transport));
        var windows = new WindowsSmbPlatformAdapter(new PrivilegedSmbOperations(transport));
        foreach (var result in new[] { new PrivilegedOperationResult(false, ProblemCode: PrivilegedProblemCode.HelperUnavailable),
            new PrivilegedOperationResult(true), new PrivilegedOperationResult(true, OutputBase64: "invalid base64"),
            new PrivilegedOperationResult(true, OutputBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("{broken"))) })
        {
            transport.Result = result;
            await Refuses(() => linux.ReadManagedSharesAsync(CancellationToken.None));
            await Refuses(() => linux.ReadUsersAsync(CancellationToken.None));
            await Refuses(() => windows.ReadManagedSharesAsync(CancellationToken.None));
        }
        transport.Result = new(true, OutputBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("[]")));
        TestAssert.Assert((await linux.ReadManagedSharesAsync(CancellationToken.None)).Count == 0, "Explicit valid empty list should be accepted.");
        var directory = Directory.CreateTempSubdirectory("smb-read-failure-");
        try
        {
            transport.Result = new(false, ProblemCode: PrivilegedProblemCode.HelperUnavailable);
            transport.Requests.Clear();
            var provider = new LinuxSambaFileServiceProvider(linux);
            await Refuses(() => provider.CreateShareAsync(new("NewShare", directory.FullName, null, false, true, false, []), Guid.NewGuid(), CancellationToken.None));
            TestAssert.Assert(transport.Requests.All(request => request.Operation == PrivilegedOperationKind.SmbReadManagedConfiguration),
                "A failed shared configuration read must not send a replacement configuration to the Helper.");
        }
        finally { directory.Delete(); }
        Console.WriteLine("SMB exact wire and failed-read protection checks passed.");
    }
    private static async Task Refuses<T>(Func<Task<T>> read)
    {
        var refused = false;
        try { await read(); }
        catch (FileServiceReadException) { refused = true; }
        TestAssert.Assert(refused, "Missing, failed, or malformed SMB configuration was treated as empty.");
    }
    private sealed class Transport : IPrivilegedOperationTransport
    {
        internal PrivilegedOperationResult Result = new(false, ProblemCode: PrivilegedProblemCode.HelperUnavailable);
        internal List<PrivilegedOperationRequest> Requests = [];
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(Result); }
    }
}
