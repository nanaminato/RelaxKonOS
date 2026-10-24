using RelaxKonOS.Server.Files;
using RelaxKonOS.Protocol.Git;

public static class TextEditorChecks
{
    private sealed class TestSystemModeResolver : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => true;
    }
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine($"PASS TEXT {++count}: {message}"); }
        void Reject(Action action, string message)
        {
            try { action(); } catch (Exception error) when (error is ArgumentException or InvalidDataException)
            { Check(true, message); return; }
            throw new Exception(message);
        }
        foreach (var encoding in new[] { "utf-8", "utf-16le", "utf-16be", "utf-32le", "utf-32be" })
        {
            var bytes = TextFileCodec.Encode("hello 世界 🐱\r\nline\r\n", encoding, true);
            var text = TextFileCodec.Decode("a.conf", bytes);
            Check(text.Encoding == encoding && text.Bom && text.Newline == "crlf" && text.Content == "hello 世界 🐱\r\nline\r\n"
                && TextFileCodec.Encode(text.Content, text.Encoding, text.Bom).SequenceEqual(bytes), encoding + " exact byte roundtrip");
        }
        Check(TextFileCodec.Decode("a", TextFileCodec.Encode("a\nb\r\nc\r", "utf-8", false)).Newline == "mixed", "Mixed endings retained");
        Reject(() => TextFileCodec.Decode("a", [0xff, 0xff]), "Malformed UTF-8 rejected");
        Reject(() => TextFileCodec.Decode("a", [1, 2, 3]), "Binary controls rejected");
        Reject(() => TextFileCodec.Encode("bad\0", "utf-8", false), "Binary write rejected");
        Reject(() => TextFileCodec.Encode("bad\ud800", "utf-8", false), "Unpaired surrogate rejected");
        Reject(() => TextFileCodec.Encode("hello", "utf-16le", false), "Ambiguous BOM-less UTF-16 rejected");
        Reject(() => TextFileCodec.Encode("hello", "gb18030", false), "Unsupported encoding rejected");
        Reject(() => TextFileCodec.Encode(new string('界', TextFileCodec.MaximumBytes / 3 + 1), "utf-8", false), "Encoded size limit enforced");
        Reject(() => TextFileCodec.Decode("a", new byte[TextFileCodec.MaximumBytes + 1]), "Read size limit enforced");

        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var editor = new TextFileEditor(new LocalFileService(new TestSystemModeResolver()));
            var largePath = Path.Combine(root, "large.txt");
            await File.WriteAllTextAsync(largePath, new string('a', TextFileCodec.MaximumBytes + 1));
            var tooLarge = false;
            try { await editor.ReadAsync(largePath, default); } catch (InvalidDataException) { tooLarge = true; }
            Check(tooLarge, "Bounded file service rejects oversized source before transferring it");
            File.Delete(largePath);
            var path = Path.Combine(root, "配置.json");
            var file = await editor.CreateAsync(path, new("first\r\n", "utf-8", true), default);
            Check(file == await editor.ReadAsync(path, default), "Create receipt matches actual read and BOM");
            var edited = await editor.SaveAsync(path, new("next\r\n", file.Version, file.Encoding, file.Bom), default);
            Check(edited?.Content == "next\r\n" && edited.Newline == "crlf", "Conditional save persists format");
            Check(await editor.SaveAsync(path, new("stale", file.Version, file.Encoding, file.Bom), default) is null,
                "Stale version refuses overwrite");
            Check((await editor.ReadAsync(path, default)).Content == "next\r\n", "Conflict leaves bytes intact");
            var refused = false;
            try { await editor.CreateAsync(path, new("replace", "utf-8", false), default); } catch (IOException) { refused = true; }
            Check(refused && (await editor.ReadAsync(path, default)).Content == "next\r\n", "Create cannot replace existing target");
            Check(Directory.GetFiles(root).Length == 1, "Failed create removes staging file");
            var copyPath = Path.Combine(root, "copy.txt");
            var copy = await editor.CreateAsync(copyPath, new(edited!.Content, "utf-16be", true), default);
            Check(copy.Encoding == "utf-16be" && copy.Content == edited.Content, "Save-as changes encoding without changing source");

            var conditional = new ConditionalFileEditor(new LocalFileService(new TestSystemModeResolver()));
            var rawPath = Path.Combine(root, "source.cs");
            byte[] originalBytes = [0xff, 0xfe, 0x41, 0x00, 0x0d, 0x00, 0x0a, 0x00];
            var created = await conditional.SaveAsync(new(rawPath, originalBytes, null), default);
            Check(created!.Version == GitTextFileWrite.Version(originalBytes)
                && (await File.ReadAllBytesAsync(rawPath)).SequenceEqual(originalBytes), "Byte-preserving conditional creation retains the original encoding and BOM");
            byte[] replacement = [0x41, 0x0a];
            var receipt = await conditional.SaveAsync(new(rawPath, replacement, created.Version), default);
            Check(receipt!.Version == GitTextFileWrite.Version(replacement), "Conditional replacement returns the new content version");
            Check(await conditional.SaveAsync(new(rawPath, originalBytes, created.Version), default) is null
                && (await File.ReadAllBytesAsync(rawPath)).SequenceEqual(replacement), "Stale editor version cannot overwrite a changed file");
            var createRefused = await conditional.SaveAsync(new(rawPath, originalBytes, null), default) is null;
            Check(createRefused && (await File.ReadAllBytesAsync(rawPath)).SequenceEqual(replacement), "Save-as cannot overwrite an existing file");
            var oversizeRefused = false;
            try { await conditional.SaveAsync(new(rawPath, new byte[TextFileCodec.MaximumBytes + 1], receipt.Version), default); }
            catch (InvalidDataException) { oversizeRefused = true; }
            Check(oversizeRefused, "Editor writes enforce the byte limit before mutation");
            var versionRefused = false;
            try { await conditional.SaveAsync(new(rawPath, replacement, "invalid"), default); }
            catch (ArgumentException) { versionRefused = true; }
            Check(versionRefused, "Editor write rejects malformed expected versions");
            var relativeRefused = false;
            try { await conditional.SaveAsync(new("relative.cs", replacement, null), default); }
            catch (ArgumentException) { relativeRefused = true; }
            Check(relativeRefused, "Editor creation rejects relative destinations");
            Check(Directory.GetFiles(root).Length == 3, "Failed exclusive creation cleans its staging file");
        }
        finally { Directory.Delete(root, recursive: true); }
        Check(JsonSerializer.Serialize(new SaveTextFileRequest("x", new string('a', 64), "utf-8", false), RelaxKonOSJsonOptions.Default)
            .Contains("\"expectedVersion\""), "Current shared camelCase contract");
        var conditionalJson = JsonSerializer.Serialize(new ConditionalFileWriteRequest("/project/a.txt", [0xef, 0xbb, 0xbf, 65], null), RelaxKonOSJsonOptions.Default);
        var conditionalRoundtrip = JsonSerializer.Deserialize<ConditionalFileWriteRequest>(conditionalJson, RelaxKonOSJsonOptions.Default)!;
        Check(conditionalJson.Contains("\"expectedVersion\":null") && conditionalRoundtrip.Content.SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf, 65 }),
            "Conditional write has one explicit byte-preserving wire shape, including null for creation");
        var incompleteRefused = false;
        try { JsonSerializer.Deserialize<ConditionalFileWriteRequest>("{\"path\":\"/project/a.txt\",\"content\":\"QQ==\"}", RelaxKonOSJsonOptions.Default); }
        catch (JsonException) { incompleteRefused = true; }
        Check(incompleteRefused, "Conditional write requires an explicit expectedVersion field");
        var readRequest = new UserExecutionRequest(new(HostPlatformKind.Linux, "1000", "alice", "/home/alice"),
            UserExecutionOperationKind.FileReadText, Path: "/home/alice/a.txt", OperationId: Guid.NewGuid());
        Check(UserExecutionRequestPolicy.IsValid(readRequest, false)
            && !UserExecutionRequestPolicy.IsValid(readRequest with { ContentBase64 = "eA==" }, false)
            && !UserExecutionRequestPolicy.IsValid(readRequest with { ExpectedBytes = 9999999 }, false), "Helper text read has a fixed closed request shape");
    }
}
