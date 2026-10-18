using System.IO.Pipes;
using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class MihomoAuthorizationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        async Task Denied(Func<Task> action, string code = "runtime.authorization_unavailable")
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == code) { count++; return; }
            throw new Exception("Expected typed authorization refusal: " + code);
        }
        var material = new MihomoPublishedRuntime("mixed-port: 17890\n", ["with_gvisor"], 9090,
            "private-controller-credential", [new("127.0.0.1", 17890)]);
        var current = new MihomoCurrentAuthorization(DateTimeOffset.UtcNow.AddSeconds(30), material);
        var reads = 0;
        await using (var server = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(Guid.NewGuid(), _ =>
        { reads++; return Task.FromResult(current); }))
        {
            Check(server.Channel.RuntimeId == "mihomo", "Channel descriptor binds the typed runtime");
            Check(!server.Channel.ToString().Contains(server.Channel.Secret, StringComparison.Ordinal)
                && !material.ToString().Contains(material.ControllerSecret, StringComparison.Ordinal)
                && !current.ToString().Contains(material.ControllerSecret, StringComparison.Ordinal),
                "Diagnostic record formatting redacts channel and published credentials");
            var received = await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(server.Channel, server.Channel.OperationId, default);
            Check(received.Published!.Yaml == material.Yaml && received.Published.ControllerSecret == material.ControllerSecret
                && received.Published.RequiredBuildTags.SequenceEqual(material.RequiredBuildTags)
                && received.Published.Listeners.SequenceEqual(material.Listeners), "Protected Mihomo material round-trips through the authenticated pipe");
            current = current with { Published = material with { Yaml = "mixed-port: 17891\n", Listeners = [new("127.0.0.1", 17891)] } };
            Check((await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(server.Channel, server.Channel.OperationId, default))
                .Published!.Yaml == current.Published.Yaml && reads == 2, "Each challenge reads the fresh published state");
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel, server.Channel.OperationId, default));
            Check(reads == 2, "Wrong typed runtime is refused before the callback reads any secret");
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<SingBoxCurrentAuthorization>(server.Channel with { RuntimeId = "sing-box" }, server.Channel.OperationId, default));
            Check(reads == 2, "Changing the descriptor runtime cannot forge its authentication context");
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(server.Channel, Guid.NewGuid(), default));
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(server.Channel with { Secret = new string('0', 64) }, server.Channel.OperationId, default));
            current = current with { ValidUntil = DateTimeOffset.UtcNow.AddSeconds(-1) };
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(server.Channel, server.Channel.OperationId, default));
            current = current with { ValidUntil = DateTimeOffset.UtcNow.AddMinutes(6) };
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(server.Channel, server.Channel.OperationId, default));
            current = current with { ValidUntil = DateTimeOffset.UtcNow.AddSeconds(30), Published = material with { RequiredBuildTags = null! } };
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(server.Channel, server.Channel.OperationId, default));
        }
        await using (var unavailable = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(Guid.NewGuid(), _ =>
            Task.FromException<MihomoCurrentAuthorization>(new RuntimePolicyException("runtime.publication_unconfirmed"))))
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(unavailable.Channel, unavailable.Channel.OperationId, default), "runtime.publication_unconfirmed");
        await using (var unavailable = new RuntimeAuthorizationServer<MihomoCurrentAuthorization>(Guid.NewGuid(), _ =>
            Task.FromException<MihomoCurrentAuthorization>(new IOException("private diagnostic and credentials"))))
            await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(unavailable.Channel, unavailable.Channel.OperationId, default));

        foreach (var invalid in new[]
        {
            material with { Yaml = "" }, material with { Yaml = "nul\0" }, material with { ControllerPort = 0 },
            material with { ControllerSecret = "short" }, material with { ControllerSecret = "secret-with-control\n" },
            material with { RequiredBuildTags = ["with_gvisor", "with_gvisor"] }, material with { RequiredBuildTags = ["unsafe tag"] },
            material with { Listeners = [] }, material with { Listeners = [new("hostname.example", 17890)] },
            material with { Listeners = [new("127.0.0.1", 65536)] },
            material with { Listeners = [new("127.0.0.1", 17890), new("127.0.0.1", 17890)] },
        }) await Denied(() => { MihomoRuntimeMaterial.Validate(invalid); return Task.CompletedTask; });

        // A peer with the test key can sign bytes; it still cannot replace the current nested contract.
        var channel = new RuntimeAuthorizationChannel(Guid.NewGuid(), Guid.NewGuid(), "mihomo", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        await using var pipe = new NamedPipeServerStream("RelaxKonOS.RuntimeAuthorization." + channel.ChannelId.ToString("N"), PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var forged = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync();
            var header = new byte[4]; await pipe.ReadExactlyAsync(header);
            var input = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await pipe.ReadExactlyAsync(input);
            using var challenge = JsonDocument.Parse(input);
            var nonce = challenge.RootElement.GetProperty("Nonce").GetString();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Grant = new { ValidUntil = DateTimeOffset.UtcNow.AddSeconds(30), Published = new
                { material.Yaml, material.RequiredBuildTags, material.ControllerPort, material.ControllerSecret, material.Listeners, UnexpectedPath = "/host-secret" } },
                ProblemCode = (string?)null
            });
            var context = Encoding.UTF8.GetBytes($"RelaxKonOS.RuntimeAuthorization.v2\nmihomo\nserver\n{channel.ChannelId:N}\n{channel.OperationId:N}\n{nonce}\n");
            var proof = Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(channel.Secret), context.Concat(bytes).ToArray()));
            var response = JsonSerializer.SerializeToUtf8Bytes(new { Content = Convert.ToBase64String(bytes), Proof = proof });
            BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
            await pipe.WriteAsync(header); await pipe.WriteAsync(response); await pipe.FlushAsync();
        });
        await Denied(async () => await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(channel, channel.OperationId, default));
        await forged;
        return count;
    }
}
