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
        Check(support.SortVersion == "1.14.3" && support.Supported && support.Capability == SingBoxRuntimeSupport.Capability,
            "Sortable versions only imply lifecycle support inside the measured native-format range");
        var outside = RuntimeSourceOperations.ConfigurationSupport(release with { OriginalVersion = "v1.12.9" });
        Check(outside.SortVersion == "1.12.9" && !outside.Supported && outside.Capability == "",
            "An older stable release stays sortable but outside the measured range");
        Check(RuntimeSourceOperations.ConfigurationSupport(release with { RuntimeId = "other-runtime" }).SortVersion == "", "Other runtime version schemes are not parsed as sing-box");
        Check(RuntimeSourceOperations.ConfigurationSupport(release with { OriginalVersion = "nightly-20261017" }).SortVersion == "", "Unknown sing-box names remain unsupported");
        Reject("release_channel_invalid", () => RuntimeSourceOperations.ConfigurationSupport(release with { OriginalVersion = "v1.15.0-alpha.1" }));

        // Mihomo has its own tag rule, its own measured native-format range and its own '-v' report.
        Check(MihomoNativeValidation.TryTag("v1.19.30", out var mihomoTag) && mihomoTag.Normalized == "1.19.30",
            "Mihomo explicitly handles its v tag prefix");
        foreach (var unknown in new[] { "1.19.30", "vv1.19.30", "Prerelease-Alpha", "v1.19", "v1.19.30.1", "v1.19.30 " })
            Check(!MihomoNativeValidation.TryTag(unknown, out _), "Not a Mihomo version tag: " + unknown);
        Check(MihomoRuntimeSupport.CapabilityFor("1.19.0") == MihomoRuntimeSupport.Capability
            && MihomoRuntimeSupport.CapabilityFor("1.19.30") == MihomoRuntimeSupport.Capability
            && MihomoRuntimeSupport.CapabilityFor("1.18.10") is null
            && MihomoRuntimeSupport.CapabilityFor("1.20.0") is null
            && MihomoRuntimeSupport.CapabilityFor("1.20.0-alpha.1") is null
            && MihomoRuntimeSupport.CapabilityFor(null) is null,
            "Only the measured stable Mihomo range receives a capability");
        const string mihomoReport = "Mihomo Meta v1.19.30 linux amd64 with go1.26.6 Sun Aug 16 09:58:25 UTC 2026\nUse tags: with_gvisor\n";
        MihomoBuildEvidence Mihomo(string output) => MihomoNativeValidation.Parse(output, "v1.19.30", "linux", "amd64");
        var mihomoBuild = Mihomo(mihomoReport);
        Check(mihomoBuild is { Version: "1.19.30", Os: "linux", Architecture: "amd64", GoVersion: "go1.26.6" }
            && mihomoBuild.BuildTags.SetEquals(["with_gvisor"]), "Actual Mihomo version, platform and build tags parsed");
        Check(Mihomo(mihomoReport.Replace("\n", "\r\n", StringComparison.Ordinal)).Architecture == "amd64",
            "Native Windows line endings accepted for Mihomo");
        Reject("native_version_mismatch", () => Mihomo(mihomoReport.Replace("v1.19.30", "v1.19.31", StringComparison.Ordinal)));
        Reject("native_platform_mismatch", () => Mihomo(mihomoReport.Replace("linux amd64", "windows amd64", StringComparison.Ordinal)));
        Reject("native_platform_mismatch", () => Mihomo(mihomoReport.Replace("linux amd64", "linux arm64", StringComparison.Ordinal)));
        Reject("native_version_invalid", () => Mihomo("Mihomo Meta v1.19.30 linux amd64\n"));
        Reject("native_version_invalid", () => Mihomo(mihomoReport + "Unexpected: payload\n"));
        Reject("native_version_invalid", () => Mihomo(mihomoReport + new string('a', 9000)));
        Reject("native_features_invalid", () => Mihomo(mihomoReport.Replace("with_gvisor", "with_gvisor,with gvisor", StringComparison.Ordinal)));
        Reject("native_version_invalid", () => Mihomo(mihomoReport + "\u001b[31m"));
        var mihomoRelease = release with { RuntimeId = "mihomo", OriginalVersion = "v1.19.30" };
        Check(RuntimeSourceOperations.ConfigurationSupport(mihomoRelease) is { SortVersion: "1.19.30", Supported: true, Capability: MihomoRuntimeSupport.Capability },
            "Mihomo discovery grants only its declared native-format range");
        Check(RuntimeSourceOperations.ConfigurationSupport(mihomoRelease with { OriginalVersion = "Prerelease-Alpha" }) is { SortVersion: "", Supported: false },
            "The upstream Mihomo alpha channel is discoverable but never installable");
        Reject("release_channel_invalid", () => RuntimeSourceOperations.ConfigurationSupport(mihomoRelease with { OriginalVersion = "v1.20.0-alpha.1" }));
        return count;
    }
}
