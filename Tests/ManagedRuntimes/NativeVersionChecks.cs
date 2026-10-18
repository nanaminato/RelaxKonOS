using System.Text;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class NativeVersionChecks
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Reject(string code, Action action)
        {
            try { action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == "runtime." + code) { count++; return; }
            throw new Exception("Expected native/version rejection: " + code);
        }
        var sequence = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.9.0", "1.10.0", "2.0.0" };
        var versions = new List<RuntimeSemanticVersion>();
        foreach (var text in sequence)
        { Check(RuntimeSemanticVersion.TryParse(text, out var version), "SemVer accepted: " + text); versions.Add(version); }
        Check(versions.AsEnumerable().Reverse().Order().Select(v => v.Normalized).SequenceEqual(sequence), "Numeric and prerelease ordering follows SemVer precedence");
        foreach (var invalid in new[] { "", "1", "1.2", "01.2.3", "1.02.3", "1.2.03", "+1.2.3", "1.2.3-", "1.2.3+", "1.2.3-a..b",
            "1.2.3-a.01", "1.2.3+one+two", "1.2.3_1", "1.2.3 ", " 1.2.3", "１.2.3", "v1.2.3", "2026-10-17", "1.2.3-α", "1.2.3+a_b" })
            Check(!RuntimeSemanticVersion.TryParse(invalid, out _), "Ambiguous version not guessed: " + invalid);
        RuntimeSemanticVersion.TryParse("1.2.3+build.001", out var build1);
        RuntimeSemanticVersion.TryParse("1.2.3+other", out var build2);
        Check(build1.CompareTo(build2) == 0 && build1.Normalized == "1.2.3+build.001", "Build metadata retained but excluded from precedence");
        RuntimeSemanticVersion.TryParse("999999999999999999999999.0.0", out var huge);
        Check(huge.CompareTo(versions[^1]) > 0, "Core components do not overflow machine integers");
        Check(SingBoxNativeValidation.TryTag("v1.14.3", out var tag) && tag.Normalized == "1.14.3", "sing-box explicitly handles its v tag prefix");
        Check(!SingBoxNativeValidation.TryTag("vv1.14.3", out _), "Multiple tag prefixes not stripped");
        Check(SingBoxNativeValidation.UpdateProblem("1.14.3", "v1.14.4") is null, "Version policy distinguishes a newer version without granting configuration support");
        Check(SingBoxNativeValidation.UpdateProblem("1.14.3", "v1.14.3") == "runtime.version_unchanged", "Another release ID for the same version cannot be called an upgrade");
        Check(SingBoxNativeValidation.UpdateProblem("1.14.3", "v1.14.3+rebuilt") == "runtime.version_unchanged", "Build metadata is not a version upgrade");
        Check(SingBoxNativeValidation.UpdateProblem("1.14.3", "v1.14.2") == "runtime.update_target_not_newer", "Downgrade requires the explicit rollback action");
        Check(SingBoxNativeValidation.UpdateProblem("unknown", "v1.14.4") == "runtime.native_version_invalid", "Unknown current version is not guessed");
        var baseline = "sing-box version 1.14.3\n\nEnvironment: go1.26.0 linux/arm64\nTags: with_quic,with_utls\nRevision: abcdef1234567890\nCGO: disabled\n";
        SingBoxBuildEvidence Parse(string output) => SingBoxNativeValidation.Parse(output, "v1.14.3", "linux", "arm64");
        var evidence = Parse(baseline);
        Check(evidence.Version == "1.14.3" && evidence.Tags.Contains("with_utls") && !evidence.Cgo, "Actual version/build/platform fields parsed");
        Check(Parse(baseline.Replace("\n", "\r\n", StringComparison.Ordinal)).Architecture == "arm64", "Native Windows line endings accepted");
        Reject("native_version_mismatch", () => Parse(baseline.Replace("version 1.14.3", "version 1.14.30", StringComparison.Ordinal)));
        Reject("native_version_mismatch", () => Parse(baseline.Replace("version 1.14.3", "version 1.14.3-rc.1", StringComparison.Ordinal)));
        Reject("native_platform_mismatch", () => Parse(baseline.Replace("linux/arm64", "linux/amd64", StringComparison.Ordinal)));
        Reject("native_platform_mismatch", () => Parse(baseline.Replace("linux/arm64", "windows/arm64", StringComparison.Ordinal)));
        Reject("native_version_invalid", () => Parse(baseline + "CGO: disabled\n"));
        Reject("native_version_invalid", () => Parse(baseline + "Unrecognized: payload\n"));
        Reject("native_version_invalid", () => Parse(baseline.Replace("CGO: disabled\n", "", StringComparison.Ordinal)));
        Reject("native_version_invalid", () => Parse(baseline.Replace("abcdef1234567890", "not-a-revision", StringComparison.Ordinal)));
        Reject("native_features_invalid", () => Parse(baseline.Replace("with_quic,with_utls", "with_utls,with_utls", StringComparison.Ordinal)));
        Reject("native_features_invalid", () => Parse(baseline.Replace("with_quic,with_utls", "with_quic,", StringComparison.Ordinal)));
        Reject("native_version_invalid", () => Parse(baseline + new string('a', 8192)));
        Reject("native_version_invalid", () => Parse(baseline + "\u001b[31m"));
        var noTags = Parse(baseline.Replace("Tags: with_quic,with_utls\n", "", StringComparison.Ordinal));
        void Features(SingBoxBuildEvidence build, string json) => SingBoxNativeValidation.RequireConfigurationFeatures(build, Encoding.UTF8.GetBytes(json));
        Features(evidence, "{\"inbounds\":[{\"type\":\"hysteria2\"},{\"type\":\"vless\",\"tls\":{\"reality\":{\"enabled\":true}}}]}" );
        Check(true, "Both required protocol build features present");
        Reject("native_feature_missing", () => Features(noTags, "{\"inbounds\":[{\"type\":\"hysteria2\"}]}"));
        Reject("native_feature_missing", () => Features(noTags, "{\"inbounds\":[{\"type\":\"vless\",\"tls\":{\"reality\":{\"enabled\":true}}}]}"));
        Features(noTags, "{\"inbounds\":[{\"type\":\"vless\",\"tls\":{\"reality\":{\"enabled\":false}}}]}" );
        Check(true, "Disabled REALITY does not demand an unused feature");
        foreach (var invalid in new[] { "{}", "{bad", "[]", "{\"inbounds\":true}", "{\"inbounds\":[{}]}" })
            Reject("native_configuration_invalid", () => Features(evidence, invalid));
        var release = new ManifestRelease("r-1", "sing-box", "v1.14.3", RuntimeChannel.Stable,
            new("linux", "arm64", null, "glibc", RuntimeInstallMode.Archive, "systemd"),
            new("a-1", new string('a', 64), 1024, "tar.gz", "sing-box", "key-1",
                new(1024, 4, [new("sing-box", 4, new string('b', 64), true)])), "https://example.com/runtime.tar.gz", null);
        var support = RuntimeSourceOperations.ConfigurationSupport(release);
        Check(support.SortVersion == "1.14.3" && !support.Supported, "Sortable versions do not imply lifecycle support");
        Check(RuntimeSourceOperations.ConfigurationSupport(release with { RuntimeId = "other-runtime" }).SortVersion == "", "Other runtime version schemes are not parsed as sing-box");
        Check(RuntimeSourceOperations.ConfigurationSupport(release with { OriginalVersion = "nightly-20261017" }).SortVersion == "", "Unknown sing-box names remain unsupported");
        Reject("release_channel_invalid", () => RuntimeSourceOperations.ConfigurationSupport(release with { OriginalVersion = "v1.15.0-alpha.1" }));
        return count;
    }
}
