using RelaxKonOS.Server.Files;

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
        }
        finally { Directory.Delete(root, recursive: true); }
        Check(JsonSerializer.Serialize(new SaveTextFileRequest("x", new string('a', 64), "utf-8", false), RelaxKonOSJsonOptions.Default)
            .Contains("\"expectedVersion\""), "Current shared camelCase contract");
        var readRequest = new UserExecutionRequest(new(HostPlatformKind.Linux, "1000", "alice", "/home/alice"),
            UserExecutionOperationKind.FileReadText, Path: "/home/alice/a.txt", OperationId: Guid.NewGuid());
        Check(UserExecutionRequestPolicy.IsValid(readRequest, false)
            && !UserExecutionRequestPolicy.IsValid(readRequest with { ContentBase64 = "eA==" }, false)
            && !UserExecutionRequestPolicy.IsValid(readRequest with { ExpectedBytes = 9999999 }, false), "Helper text read has a fixed closed request shape");
    }
}
