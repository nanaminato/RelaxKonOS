using System.IO.Pipes;
using RelaxKonOS.Protocol.ProcessGuardian;
using RelaxKonOS.Client.Apps.ProcessGuardian;

public static class GuardianReadChecks
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine($"PASS GUARDIAN {++count}: {label}"); }
        async Task Reject(Func<Task> read, string code, string label)
        {
            try { await read(); }
            catch (GuardianReadException exception) { Check(exception.ProblemCode == code, label); return; }
            throw new Exception(label);
        }
        async Task WithResponse(GuardianAgentResponse response, string command, Func<IProcessGuardianService, Task> read)
        {
            var pipeName = "relaxkonos-guardian-test-" + Guid.NewGuid().ToString("N");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var responder = Task.Run(async () =>
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var request = JsonSerializer.Deserialize<GuardianAgentRequest>((await reader.ReadLineAsync(timeout.Token))!, RelaxKonOSJsonOptions.Default)!;
                if (request.Command != command || request.SharedSecret != "test-secret") throw new Exception("Unexpected isolated IPC request.");
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, RelaxKonOSJsonOptions.Default));
            }, timeout.Token);
            await read(new NamedPipeProcessGuardianService(new GuardianAgentOptions { PipeName = pipeName, SharedSecret = "test-secret" }));
            await responder;
        }
        var reads = new (string Command, Func<IProcessGuardianService, Task> Read)[]
        {
            ("list", async service => { await service.ListWorkloadsAsync(); }),
            ("logs", async service => { await service.ListLogsAsync("job"); }),
            ("audit", async service => { await service.ListAuditAsync(); }),
        };
        foreach (var (command, read) in reads)
        {
            await WithResponse(new GuardianAgentResponse(false, "guardian.agent_timeout"), command,
                service => Reject(() => read(service), "guardian.agent_timeout", command + " failure does not become an empty collection"));
            await WithResponse(new GuardianAgentResponse(true, ""), command,
                service => Reject(() => read(service), "guardian.agent_invalid_response", command + " missing successful payload rejected"));
            await Reject(() => read(new UnavailableProcessGuardianService()), "guardian.agent_not_installed", command + " unavailable provider rejected");
        }
        await WithResponse(new GuardianAgentResponse(true, "", Workloads: []), "list", async service =>
            Check((await service.ListWorkloadsAsync()).Count == 0, "Authoritative empty workload list remains valid"));
        await WithResponse(new GuardianAgentResponse(true, "", Logs: []), "logs", async service =>
            Check((await service.ListLogsAsync("job")).Count == 0, "Authoritative empty log snapshot remains valid"));
        await WithResponse(new GuardianAgentResponse(true, "", Audits: []), "audit", async service =>
            Check((await service.ListAuditAsync()).Count == 0, "Authoritative empty audit list remains valid"));
        var definition = new ProcessDefinitionDto("job", "Job", "/bin/job", ["", " spaced ", "a\nb"], "/work", true, 49, 17,
            new GuardianHealthCheckDto("http", "https://host/health", 37, 11, 9), "alice", "uid:1042");
        ProcessDefinitionDto Edit(string text, string account = "alice", bool windows = false) => GuardianDefinitionEditor.Merge(definition,
            "job", "Renamed", "/bin/job", text, "/work", true, account, windows);
        var merged = Edit(string.Join(Environment.NewLine, definition.Arguments));
        Check(merged.Arguments.SequenceEqual(definition.Arguments), "Desktop unedited empty and multiline argument array preserved");
        Check(merged.StopTimeoutSeconds == 49 && merged.MaxRestartAttempts == 17 && merged.HealthCheck == definition.HealthCheck && merged.RunAsIdentity == definition.RunAsIdentity &&
            GuardianDefinitionEditor.Merge(definition, "job", "Name", "/bin/job", "", "/work with trailing space ", false, "alice", false).WorkingDirectory == "/work with trailing space ",
            "Desktop editing preserves stop timeout, restart policy, all health fields and stable identity");
        Check(Edit(" one \r\n\r\nlast\n").Arguments.SequenceEqual(new[] { " one ", "", "last", "" }), "Desktop edited line arguments preserve empty values and whitespace");
        Check(Edit("", "ALICE").RunAsIdentity is null && Edit("", "ALICE", true).RunAsIdentity == "uid:1042", "Desktop identity follows host case comparison");
        var rejectedTarget = false;
        try { GuardianDefinitionEditor.Merge(definition, "another", "Name", "/bin/job", "", "/work", false, "alice", false); }
        catch (InvalidOperationException) { rejectedTarget = true; }
        Check(rejectedTarget, "Desktop immutable editor target cannot change");
        var noConfiguration = new NamedPipeProcessGuardianService(new GuardianAgentOptions());
        await Reject(async () => { await noConfiguration.ListWorkloadsAsync(); }, "guardian.agent_not_configured", "Missing IPC configuration remains explicit");
        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Path.GetTempPath(), "relaxkonos-guardian-definition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var options = new RelaxKonOS.Guardian.Agent.GuardianAgentOptions("unused", "test-secret", root, false,
                    new RelaxKonOS.Guardian.Agent.ProtectedServerMonitorOptions());
                var agent = new RelaxKonOS.Guardian.Agent.WorkloadSupervisor(options);
                var request = definition with { ExecutablePath = Environment.ProcessPath!, WorkingDirectory = root,
                    EnabledOnBoot = false, RunAs = identity.Name, RunAsIdentity = identity.User!.Value };
                var response = await agent.HandleAsync(new GuardianAgentRequest("test-secret", "upsert", Definition: request), CancellationToken.None);
                Check(response.Success && response.Definition is not null, "Actual Agent upsert receipt includes its saved declaration without starting a process");
                var receipt = response.Definition!;
                Check(receipt.Arguments.SequenceEqual(request.Arguments) && receipt.HealthCheck == request.HealthCheck && receipt.RunAsIdentity == request.RunAsIdentity,
                    "Actual Agent receipt preserves arguments, health configuration and approved SID");
                var readback = await agent.HandleAsync(new GuardianAgentRequest("test-secret", "definition", request.Id), CancellationToken.None);
                Check(JsonSerializer.Serialize(receipt, RelaxKonOSJsonOptions.Default) == JsonSerializer.Serialize(readback.Definition, RelaxKonOSJsonOptions.Default),
                    "Actual Agent definition readback matches this save receipt exactly");
                var persisted = JsonSerializer.Deserialize<ProcessDefinitionDto[]>(await File.ReadAllTextAsync(Path.Combine(root, "workloads.json")), RelaxKonOSJsonOptions.Default)!;
                Check(persisted.Length == 1 && JsonSerializer.Serialize(persisted[0], RelaxKonOSJsonOptions.Default) == JsonSerializer.Serialize(receipt, RelaxKonOSJsonOptions.Default),
                    "Actual Agent persisted definition matches the receipt");
            }
            finally { Directory.Delete(root, recursive: true); }
        }
        Console.WriteLine($"Guardian read and desktop definition checks passed: {count}.");
    }
}
