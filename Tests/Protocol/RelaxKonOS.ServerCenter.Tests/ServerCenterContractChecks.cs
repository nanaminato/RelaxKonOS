using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.ServerCenter.Tests;

/// <summary>G0 部署契约的契约测试：manifest、路径、并发/中断不变量、发布包完整性与稳定登录身份。</summary>
internal static class ServerCenterContractChecks
{
    public static void Run()
    {
        VerifyLinuxSystems();
        VerifyReleaseManifest();
        VerifyInputRules();
        VerifyInstallationIdentity();
        VerifyConnectionIdentity();
        VerifyLifecycle();
        VerifyRetentionPolicy();
        VerifyModeMatrix();
        VerifyReleaseDescriptor();
        VerifyArchive();
        VerifyJsonContract();
        VerifyOperationRecovery();
        VerifyProblemCodes();
        VerifyHostTrust();
        VerifyTunnelResolution();
        VerifyHostTarget();
        Console.WriteLine("ServerCenter 部署契约检查通过。");
    }

    public static void VerifyLinuxSystems()
    {
        foreach (var system in ServerHostPlatformSupport.SupportedLinuxSystems)
        {
            var parts = system.Split('-', 2);
            Check(ServerHostPlatformSupport.IsSupportedLinuxSystem(parts[0], parts[1]), $"Supported host: {system}");
        }
        Check(ServerHostPlatformSupport.IsSupportedLinuxSystem("debian", "13"), "Debian 13 accepted");
        foreach (var version in new[] { "21", "21.1", "21.2", "21.3", "22", "22.1", "22.2", "22.3" })
            Check(ServerHostPlatformSupport.IsSupportedLinuxSystem("linuxmint", version), $"Mint {version} accepted");
        foreach (var (id, version) in new[] { ("debian", "11"), ("debian", "14"), ("linuxmint", "20.3"), ("linuxmint", "23"), ("linuxmint", "7"), ("rocky", "9"), ("ubuntu", "20.04") })
            Check(!ServerHostPlatformSupport.IsSupportedLinuxSystem(id, version), $"Unsupported host rejected: {id}-{version}");
        Check(!ServerHostPlatformSupport.IsSupportedLinuxSystem(null, "13"), "Missing ID rejected");
        Check(!ServerHostPlatformSupport.IsSupportedLinuxSystem("debian", null), "Missing version rejected");
    }

    private static void VerifyReleaseManifest()
    {
        var valid = new ServerReleaseManifestDto(
            ServerDeploymentProtocol.Version, ServerReleasePackageKind.Server, "0.1.0",
            ServerRuntimeIdentifier.LinuxX64, ["debian-12", "ubuntu-24.04"],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["linux"] = new Dictionary<string, string> { ["server"] = "payload/linux/server/RelaxKonOS.Server" } },
            DateTimeOffset.UnixEpoch,
            [new ServerReleaseFileDto("payload/linux/server/RelaxKonOS.Server", 1024, new string('a', 64))]);

        Check(ServerReleaseValidation.ValidateManifest(valid, ServerRuntimeIdentifier.LinuxX64) is null,
            "合法 manifest 通过校验");
        Check(ServerReleaseValidation.ValidateManifest(valid, ServerRuntimeIdentifier.WinX64)
                == ServerDeploymentProblemCodes.PackageRuntimeMismatch,
            "RID 不匹配被拒绝");
        Check(ServerReleaseValidation.ValidateManifest(valid with { SchemaVersion = 99 }, ServerRuntimeIdentifier.LinuxX64)
                == ServerDeploymentProblemCodes.PackageManifestInvalid,
            "错误 schemaVersion 被拒绝");
        Check(ServerReleaseValidation.ValidateManifest(valid with { Files = [] }, ServerRuntimeIdentifier.LinuxX64)
                == ServerDeploymentProblemCodes.PackageManifestInvalid,
            "空文件清单被拒绝");
        Check(ServerReleaseValidation.ValidateManifest(valid with
                { Payload = new Dictionary<string, IReadOnlyDictionary<string, string>>() },
                ServerRuntimeIdentifier.LinuxX64) == ServerDeploymentProblemCodes.PackageManifestInvalid,
            "缺失目标平台 payload 被拒绝");
        Check(ServerReleaseValidation.ValidateManifest(valid with
                { Files = [valid.Files[0], valid.Files[0]] }, ServerRuntimeIdentifier.LinuxX64)
                == ServerDeploymentProblemCodes.PackageManifestInvalid,
            "重复清单路径被拒绝");
        Check(ServerReleaseValidation.ValidateManifest(valid with { Version = "not a version!" }, ServerRuntimeIdentifier.LinuxX64)
                == ServerDeploymentProblemCodes.PackageManifestInvalid,
            "非法版本号被拒绝");
        Check(ServerReleaseValidation.ValidateManifest(null, ServerRuntimeIdentifier.LinuxX64)
                == ServerDeploymentProblemCodes.PackageManifestInvalid,
            "缺失 manifest 被拒绝");

        var traversal = valid with { Files = [new ServerReleaseFileDto("../etc/passwd", 1, new string('a', 64))] };
        Check(ServerReleaseValidation.ValidateManifest(traversal, ServerRuntimeIdentifier.LinuxX64)
                == ServerDeploymentProblemCodes.PackageLayoutUnsafe,
            "路径穿越条目被拒绝");
        var backslash = valid with { Files = [new ServerReleaseFileDto("payload\\server", 1, new string('a', 64))] };
        Check(ServerReleaseValidation.ValidateManifest(backslash, ServerRuntimeIdentifier.LinuxX64)
                == ServerDeploymentProblemCodes.PackageLayoutUnsafe,
            "反斜杠条目被拒绝");
        var badDigest = valid with { Files = [new ServerReleaseFileDto("payload/server", 1, "xyz")] };
        Check(ServerReleaseValidation.ValidateManifest(badDigest, ServerRuntimeIdentifier.LinuxX64)
                == ServerDeploymentProblemCodes.PackageManifestInvalid,
            "非法文件摘要被拒绝");
    }

    private static void VerifyInputRules()
    {
        Check(ServerDeploymentInputRules.IsSafeStagedPackageName("relaxkonos-0.1.0-win-x64.zip"), "合法暂存包名通过");
        Check(!ServerDeploymentInputRules.IsSafeStagedPackageName("../evil.zip"), "路径穿越包名被拒绝");
        Check(!ServerDeploymentInputRules.IsSafeStagedPackageName("sub/dir.zip"), "子目录包名被拒绝");
        Check(!ServerDeploymentInputRules.IsSafeStagedPackageName("payload.tar.gz"), "非 zip 包名被拒绝");
        Check(!ServerDeploymentInputRules.IsSafeStagedPackageName(""), "空包名被拒绝");
        Check(ServerDeploymentInputRules.IsSha256(new string('a', 64)) && ServerDeploymentInputRules.IsSha256(new string('F', 64)),
            "SHA-256 大小写均接受");
        Check(!ServerDeploymentInputRules.IsSha256(new string('a', 63)), "长度不足的摘要被拒绝");
        Check(ServerDeploymentInputRules.IsVersion("1.2.3-rc.1"), "预发布版本号通过");
        Check(!ServerDeploymentInputRules.IsVersion("abc"), "无数字的版本号被拒绝");
    }

    private static void VerifyInstallationIdentity()
    {
        var id = ServerInstallationId.NewId();
        Check(ServerInstallationId.TryNormalize(id, out var normalized) && normalized == id, "新安装标识可规范化");
        Check(!ServerInstallationId.IsValid(id.ToUpperInvariant()), "大写十六进制安装标识被拒绝");
        Check(!ServerInstallationId.IsValid("rki-" + new string('a', 31)), "长度错误的安装标识被拒绝");
        Check(!ServerInstallationId.IsValid(new string('a', 32)), "缺少前缀的安装标识被拒绝");
        Check(!ServerInstallationId.IsValid(null), "空安装标识被拒绝");
        Check(ServerInstallationId.NewId() != ServerInstallationId.NewId(), "安装标识不重复");
    }

    private static void VerifyConnectionIdentity()
    {
        Check(ServerConnectionIdentityRules.NormalizeServerUrl("HTTP://Example.COM:80/") == "http://example.com",
            "等价直连 URL 归一化到同一身份");
        Check(ServerConnectionIdentityRules.NormalizeServerUrl("https://Example.com:443/relaxkonos/")
                == "https://example.com/relaxkonos",
            "带基础路径的 URL 归一化");
        Check(ServerConnectionIdentityRules.NormalizeServerUrl("http://host:8080") == "http://host:8080",
            "非默认端口保留");

        var installationId = ServerInstallationId.NewId();
        var before = ServerConnectionIdentityRules.ManagedTunnel(installationId, "http://127.0.0.1:51000");
        var after = ServerConnectionIdentityRules.ManagedTunnel(installationId, "http://127.0.0.1:52345");
        Check(ServerConnectionIdentityRules.PreservesIdentity(before, after), "隧道换端口不改变登录身份");
        Check(before.EffectiveBaseUrl != after.EffectiveBaseUrl, "隧道换端口更新传输地址");
        Check(ServerConnectionIdentityRules.CredentialKey(before.ServiceId, "alice")
                == ServerConnectionIdentityRules.CredentialKey(after.ServiceId, "alice"),
            "隧道换端口后保险箱键不变");
        Check(before.Kind == ServerServiceIdKind.ManagedInstallation, "受管隧道身份种类正确");

        var direct = ServerConnectionIdentityRules.Direct("http://example.com/");
        Check(direct.Kind == ServerServiceIdKind.DirectUrl && direct.ServiceId == direct.EffectiveBaseUrl,
            "直连身份的 serviceId 与传输地址一致");
    }

    private static void VerifyLifecycle()
    {
        Check(ServerDeploymentLifecycle.ValidateEventProgress(
                ServerDeploymentPhase.Preflight, 5, ServerDeploymentPhase.Activating, 6) is null,
            "单调推进的事件序列合法");
        Check(ServerDeploymentLifecycle.ValidateEventProgress(
                ServerDeploymentPhase.Activating, 6, ServerDeploymentPhase.Preflight, 7)
                == ServerDeploymentProblemCodes.InvalidRequest,
            "阶段回退被拒绝");
        Check(ServerDeploymentLifecycle.ValidateEventProgress(
                ServerDeploymentPhase.Preflight, 6, ServerDeploymentPhase.Activating, 6)
                == ServerDeploymentProblemCodes.InvalidRequest,
            "序号未递增被拒绝");
        Check(ServerDeploymentLifecycle.ValidateEventProgress(
                ServerDeploymentPhase.HealthChecking, 11, ServerDeploymentPhase.RollingBack, 12) is null,
            "健康失败后回滚是单调推进");

        Check(ServerDeploymentLifecycle.IsCancellablePhase(ServerDeploymentPhase.Preflight), "预检可取消");
        Check(ServerDeploymentLifecycle.IsCancellablePhase(ServerDeploymentPhase.Transferring), "传输可取消");
        Check(!ServerDeploymentLifecycle.IsCancellablePhase(ServerDeploymentPhase.Activating), "激活临界区不可取消");
        Check(!ServerDeploymentLifecycle.IsCancellablePhase(ServerDeploymentPhase.RollingBack), "回滚临界区不可取消");
        Check(ServerDeploymentLifecycle.IsSafeRecoveryWindow(ServerDeploymentPhase.Stopping), "停服务进入安全恢复窗口");
        Check(!ServerDeploymentLifecycle.IsSafeRecoveryWindow(ServerDeploymentPhase.Preflight), "预检不在安全恢复窗口");
        Check(!ServerDeploymentLifecycle.IsCancellable(ServerDeploymentPhase.Completed, ServerDeploymentState.Succeeded),
            "终态操作不可取消");
        Check(ServerDeploymentLifecycle.IsTerminalState(ServerDeploymentState.Interrupted)
                && ServerDeploymentLifecycle.IsTerminalState(ServerDeploymentState.Failed),
            "失败与中断都是终态");
    }

    private static void VerifyRetentionPolicy()
    {
        Check(ServerDataRetentionPolicy.DefaultUninstall == ServerDataRetention.Retain, "卸载默认保留数据");
        Check(ServerDataRetentionPolicy.DeletedScopes(ServerDataRetention.Retain).Count == 0, "保留数据时不删除任何数据范围");
        Check(ServerDataRetentionPolicy.DeletedScopes(ServerDataRetention.Delete).Count
                == ServerDataRetentionPolicy.IrrecoverableScopes.Count,
            "删除数据时列出全部不可恢复范围");
        Check(ServerDataRetentionPolicy.RequiresServerNameConfirmation(ServerDataRetention.Delete), "删除数据需要输入名称确认");
        Check(!ServerDataRetentionPolicy.RequiresServerNameConfirmation(ServerDataRetention.Retain), "保留数据无需二次确认");
        Check(ServerDataRetentionPolicy.OffersExportBeforeDelete(ServerDataRetention.Delete), "删除数据前提供导出入口");
        Check(ServerDataRetentionPolicy.RetainedScopes.Contains(ServerDataScope.Database), "数据库属于可保留范围");
    }

    private static void VerifyModeMatrix()
    {
        Check(ServerDeploymentModeMatrix.All.Count == 3, "能力矩阵覆盖三种模式");
        Check(ServerDeploymentModeMatrix.All.Select(m => m.Mode).Distinct().Count() == 3, "三种模式互不相同");
        foreach (var mode in Enum.GetValues<ServerInstallMode>())
            Check(ServerDeploymentModeMatrix.For(mode).Mode == mode, $"For({mode}) 返回同一模式");

        var linuxUser = ServerDeploymentModeMatrix.LinuxUser;
        Check(!linuxUser.RequiresElevation && !linuxUser.SupportsSystemService, "User Mode 无需提升且不装系统服务");
        Check(linuxUser.HealthPath == "/ready", "User Mode 健康路径为 /ready");

        var linuxSystem = ServerDeploymentModeMatrix.LinuxSystem;
        Check(linuxSystem.RequiresElevation && linuxSystem.SupportsSudoElevation, "System Mode 支持 sudo 提权");

        var windows = ServerDeploymentModeMatrix.WindowsSystem;
        Check(windows.RequiresElevatedSshToken && windows.HostPlatform == HostPlatformKind.Windows,
            "Windows 要求已提升管理员 SSH 令牌");
        Check(ServerDeploymentModeMatrix.All.All(m => m.SupportsDataRetention && m.DefaultLoopbackOnly),
            "三种模式都支持保留数据且默认仅 loopback");
    }

    private static void VerifyReleaseDescriptor()
    {
        var descriptor = new ServerReleaseDescriptorDto(
            ServerDeploymentProtocol.Version, ServerReleasePackageKind.Server, "0.1.0",
            ServerRuntimeIdentifier.LinuxX64, "https://downloads.relaxkon.com/x.zip", new string('a', 64));
        Check(ServerReleaseValidation.ValidateDescriptor(descriptor) is null, "合法描述符通过校验");
        Check(ServerReleaseValidation.ValidateDescriptor(descriptor with { Sha256 = "bad" })
              == ServerDeploymentProblemCodes.PackageDigestMismatch, "描述符摘要格式被校验");
    }

    private static void VerifyArchive()
    {
        const string path = "payload/linux/server/RelaxKonOS.Server";
        var payload = "server payload"u8.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var manifest = new ServerReleaseManifestDto(
            ServerDeploymentProtocol.Version, ServerReleasePackageKind.Server, "0.1.0",
            ServerRuntimeIdentifier.LinuxX64, ["debian-12"],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["linux"] = new Dictionary<string, string> { ["server"] = path } },
            DateTimeOffset.UnixEpoch, [new ServerReleaseFileDto(path, payload.Length, digest)]);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, RelaxKonOSJsonOptions.Default);

        static MemoryStream Archive(byte[] manifestBytes, byte[] payload, string path, bool extra = false)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, bytes) in new[] { ("manifest.json", manifestBytes), (path, payload) })
                {
                    using var entry = zip.CreateEntry(name).Open();
                    entry.Write(bytes);
                }
                if (extra)
                {
                    using var entry = zip.CreateEntry("unlisted.txt").Open();
                    entry.Write("unlisted"u8);
                }
            }
            stream.Position = 0;
            return stream;
        }

        using var valid = Archive(manifestBytes, payload, path);
        var verified = ServerReleaseArchiveVerifier.Verify(
            valid, ServerReleasePackageKind.Server, ServerRuntimeIdentifier.LinuxX64);
        Check(verified.Verified && verified.Manifest?.Version == "0.1.0", "无签名 ZIP 的逐文件校验通过");
        var extractParent = Path.Combine(Path.GetTempPath(), "relaxkonos-release-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = Path.Combine(extractParent, "package");
            var extracted = ServerReleaseArchiveVerifier.VerifyAndExtract(valid, target,
                ServerReleasePackageKind.Server, ServerRuntimeIdentifier.LinuxX64);
            Check(extracted.Verified && File.ReadAllBytes(Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar)))
                .AsSpan().SequenceEqual(payload), "启动器只解出清单列明的文件");
            var rejectedTarget = Path.Combine(extractParent, "rejected");
            var rejected = ServerReleaseArchiveVerifier.VerifyAndExtract(valid, rejectedTarget,
                ServerReleasePackageKind.Server, ServerRuntimeIdentifier.LinuxArm64);
            Check(!rejected.Verified && !Directory.Exists(rejectedTarget), "错误架构在解包前被拒绝");
        }
        finally
        {
            if (Directory.Exists(extractParent)) Directory.Delete(extractParent, recursive: true);
        }
        Check(ServerReleaseArchiveVerifier.Verify(valid, ServerReleasePackageKind.Server,
                ServerRuntimeIdentifier.LinuxArm64).ProblemCode
              == ServerDeploymentProblemCodes.PackageRuntimeMismatch, "错误 RID 的 ZIP 被拒绝");
        using var altered = Archive(manifestBytes, "altered"u8.ToArray(), path);
        Check(ServerReleaseArchiveVerifier.Verify(altered, ServerReleasePackageKind.Server,
                ServerRuntimeIdentifier.LinuxX64).ProblemCode
              == ServerDeploymentProblemCodes.PackageManifestInvalid, "包内文件长度变化被拒绝");
        using var extra = Archive(manifestBytes, payload, path, extra: true);
        Check(ServerReleaseArchiveVerifier.Verify(extra, ServerReleasePackageKind.Server,
                ServerRuntimeIdentifier.LinuxX64).ProblemCode
              == ServerDeploymentProblemCodes.PackageLayoutUnsafe, "未列入清单的文件被拒绝");
    }

    public static void VerifyJsonContract()
    {
        var request = new ServerDeploymentRequest(
            ServerDeploymentProtocol.Version, Guid.NewGuid(), ServerDeploymentKind.Probe,
            new ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback));

        var json = JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default);
        Check(ServerDeploymentRequestWireValidation.IsStrictRequest(Encoding.UTF8.GetBytes(json)),
            "Linux 启动器接受规范请求字节");
        foreach (var kind in Enum.GetValues<ServerDeploymentKind>())
        {
            var operationRequest = request with { Kind = kind, Options = request.Options! with { Mode = ServerInstallMode.LinuxSystem } };
            var operationJson = JsonSerializer.Serialize(operationRequest, RelaxKonOSJsonOptions.Default);
            Check(ServerDeploymentRequestWireValidation.IsStrictRequest(Encoding.UTF8.GetBytes(operationJson)),
                $"{kind} 接受包含默认 removeComponents 的序列化请求");
        }
        var selective = request with { Kind = ServerDeploymentKind.Uninstall, Options = request.Options! with { RemoveComponents = "frp,mihomo" } };
        var selectiveJson = JsonSerializer.Serialize(selective, RelaxKonOSJsonOptions.Default);
        Check(ServerDeploymentRequestWireValidation.IsStrictRequest(Encoding.UTF8.GetBytes(selectiveJson)),
            "选择性卸载组件以字符串传递");
        foreach (var invalid in new[] { "null", "true", "1", "[]", "{}" })
            Check(!ServerDeploymentRequestWireValidation.IsStrictRequest(Encoding.UTF8.GetBytes(
                selectiveJson.Replace("\"removeComponents\":\"frp,mihomo\"", "\"removeComponents\":" + invalid, StringComparison.Ordinal))),
                $"removeComponents 拒绝非字符串类型 {invalid}");
        Check(!ServerDeploymentRequestWireValidation.IsStrictRequest(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"operationId\":\"" + request.OperationId + "\",\"kind\":\"probe\"}")),
            "Linux 启动器拒绝重复字段");
        Check(!ServerDeploymentRequestWireValidation.IsStrictRequest(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"operationId\":\"" + request.OperationId + "\",\"kind\":\"probe\",\"options\":{\"mode\":{\"kind\":\"install\"}}}")),
            "Linux 启动器拒绝嵌套字段伪装");
        Check(!ServerDeploymentRequestWireValidation.IsStrictRequest(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"operationId\":\"" + request.OperationId + "\",\"kind\":\"probe\",\"options\":{\"source\":0,\"network\":\"loopback\"}}")),
            "Linux 启动器拒绝数字枚举伪装");
        Check(json.Contains("\"kind\":\"probe\"", StringComparison.Ordinal), "枚举以 camelCase 字符串序列化");
        Check(json.Contains("\"network\":\"loopback\"", StringComparison.Ordinal), "网络选项以字符串序列化");

        var scopedInstall = request with
        {
            Kind = ServerDeploymentKind.Install,
            Options = request.Options! with { FileAccess = ServerFileAccessScope.Full }
        };
        var scopedJson = JsonSerializer.Serialize(scopedInstall, RelaxKonOSJsonOptions.Default);
        Check(ServerDeploymentRequestWireValidation.IsStrictRequest(Encoding.UTF8.GetBytes(scopedJson)) &&
              scopedJson.Contains("\"fileAccess\":\"full\"", StringComparison.Ordinal),
            "特权文件访问范围以严格的字符串选项传递");

        var round = JsonSerializer.Deserialize<ServerDeploymentRequest>(json, RelaxKonOSJsonOptions.Default);
        Check(round == request, "请求可无损往返序列化");

        var unknown = false;
        try
        {
            JsonSerializer.Deserialize<ServerDeploymentRequest>(
                """{"schemaVersion":1,"operationId":"00000000-0000-0000-0000-000000000000","kind":"probe","bogus":1}""",
                RelaxKonOSJsonOptions.Default);
        }
        catch (JsonException) { unknown = true; }
        Check(unknown, "未知字段被拒绝");

        var operation = new ServerDeploymentOperationDto(
            ServerDeploymentProtocol.Version, Guid.NewGuid(), ServerInstallationId.NewId(),
            ServerDeploymentKind.Install, ServerDeploymentPhase.HealthChecking, ServerDeploymentState.Running,
            12, DateTimeOffset.UnixEpoch, 80, null, "正在核验服务健康", Cancellable: false,
            StartedAtUtc: DateTimeOffset.UnixEpoch, CompletedAtUtc: null);
        var opJson = JsonSerializer.Serialize(operation, RelaxKonOSJsonOptions.Default);
        Check(JsonSerializer.Deserialize<ServerDeploymentOperationDto>(opJson, RelaxKonOSJsonOptions.Default) == operation,
            "操作记录可无损往返序列化");
        Check(JsonSerializer.Serialize(ServerRuntimeIdentifier.WinX64, RelaxKonOSJsonOptions.Default) == "\"win-x64\""
              && JsonSerializer.Deserialize<ServerRuntimeIdentifier>("\"win-x64\"", RelaxKonOSJsonOptions.Default)
              == ServerRuntimeIdentifier.WinX64, "Windows 启动器的 RID 与发布包一致");
        Check(JsonSerializer.Serialize(ServerReleasePackageKind.UserServer, RelaxKonOSJsonOptions.Default)
              == "\"user-server\"", "User Mode 包类型与发布包一致");
    }

    private static void VerifyOperationRecovery()
    {
        var operationId = Guid.NewGuid();
        var installationId = ServerInstallationId.NewId();
        var at = DateTimeOffset.UnixEpoch;
        var verified = new ServerDeploymentResultDto(
            installationId, ServerInstallMode.LinuxUser, "0.1.0", null,
            null, null, "http://127.0.0.1:5000", Healthy: true);
        var completed = new ServerDeploymentOperationDto(
            ServerDeploymentProtocol.Version, operationId, installationId,
            ServerDeploymentKind.Install, ServerDeploymentPhase.Completed, ServerDeploymentState.Succeeded,
            4, at, 100, null, "完成", Cancellable: false,
            StartedAtUtc: at, CompletedAtUtc: at, Result: verified);
        var json = JsonSerializer.Serialize(completed, RelaxKonOSJsonOptions.Default);
        Check(ServerDeploymentRecordReader.ReadRecord(json, operationId) == completed,
            "断线后可按 operationId 读取权威完成回执");
        Check(RejectsRecord(json, Guid.NewGuid()), "回执不属于当前操作时被拒绝");
        Check(RejectsRecord(JsonSerializer.Serialize(completed with { Result = verified with { Healthy = false } },
            RelaxKonOSJsonOptions.Default), operationId), "安装成功回执必须有健康证据");
        Check(RejectsRecord(JsonSerializer.Serialize(completed with { Cancellable = true },
            RelaxKonOSJsonOptions.Default), operationId), "终态不可取消");
        Check(RejectsRecord(JsonSerializer.Serialize(completed with
            {
                Kind = ServerDeploymentKind.Probe,
                Result = null,
                Probe = null
            }, RelaxKonOSJsonOptions.Default), operationId), "预检成功回执必须包含宿主事实");

        var first = new ServerDeploymentEventDto(ServerDeploymentProtocol.Version, operationId,
            installationId, ServerDeploymentKind.Install, ServerDeploymentPhase.Preflight,
            ServerDeploymentState.Running, 1, at, 20, null, "预检");
        var second = first with { Phase = ServerDeploymentPhase.HealthChecking, Sequence = 2, Progress = 80 };
        var stream = JsonSerializer.Serialize(first, RelaxKonOSJsonOptions.Default) + "\n"
            + JsonSerializer.Serialize(second, RelaxKonOSJsonOptions.Default) + "\n";
        Check(ServerDeploymentRecordReader.ReadEvents(stream, operationId).Count == 2,
            "操作事件按行解析");
        Check(ServerDeploymentRecordReader.ReadEvents(stream, operationId, afterSequence: 1)
            .Single().Sequence == 2, "重连后按 sequence 补取未见事件");
        var reversed = stream + JsonSerializer.Serialize(first with { Sequence = 3 }, RelaxKonOSJsonOptions.Default);
        var rejected = false;
        try { ServerDeploymentRecordReader.ReadEvents(reversed, operationId); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "阶段倒退的事件流被拒绝");
        var changedKind = stream + JsonSerializer.Serialize(second with
            { Kind = ServerDeploymentKind.Uninstall, Sequence = 3 }, RelaxKonOSJsonOptions.Default);
        rejected = false;
        try { ServerDeploymentRecordReader.ReadEvents(changedKind, operationId); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "同一操作中动作种类变化被拒绝");
    }

    private static bool RejectsRecord(string json, Guid expectedOperationId)
    {
        try { ServerDeploymentRecordReader.ReadRecord(json, expectedOperationId); return false; }
        catch (InvalidDataException) { return true; }
    }

    private static void VerifyHostTrust()
    {
        var keyA = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var keyB = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };

        var fingerprint = ServerHostTrustRules.Fingerprint(keyA);
        Check(ServerHostTrustRules.IsFingerprint(fingerprint), "SHA256 指纹形状合法");
        Check(fingerprint.StartsWith(ServerHostTrustRules.FingerprintPrefix, StringComparison.Ordinal)
                && fingerprint.Length == ServerHostTrustRules.FingerprintPrefix.Length + 43,
            "指纹为 SHA256: 加 43 位 Base64");
        Check(ServerHostTrustRules.GroupedFingerprint(fingerprint).Contains(' ', StringComparison.Ordinal),
            "指纹可分组展示");
        Check(ServerHostTrustRules.Fingerprint(keyA) == fingerprint, "同一密钥指纹稳定");
        Check(ServerHostTrustRules.Fingerprint(keyB) != fingerprint, "不同密钥指纹不同");
        Check(!ServerHostTrustRules.IsFingerprint("MD5:aa:bb"), "非 SHA256 指纹被拒绝");

        Check(ServerHostTrustRules.EndpointKey("Host.Example.COM", 22) == "host.example.com:22", "端点键规范化主机名");
        Check(ServerHostTrustRules.EndpointKey("[::1]", 2222) == "::1:2222", "IPv6 方括号被去除");
        Check(ServerHostTrustRules.NormalizeHost("  Node-1 ") == "node-1", "主机名去空白并小写");

        Check(ServerHostTrustRules.Evaluate([], "host.example.com", 22, "ssh-ed25519", keyA)
                == ServerHostKeyTrust.Unknown,
            "首次见到端点报告未知并要求核对");
        Check(ServerHostTrustRules.ProblemCode(ServerHostKeyTrust.Unknown)
                == ServerDeploymentProblemCodes.HostKeyUnknown,
            "未知密钥对应稳定问题码");

        var confirmed = new ServerHostKeyRecord(
            "host.example.com", 22, "ssh-ed25519", Convert.ToBase64String(keyA), fingerprint, DateTimeOffset.UnixEpoch);
        Check(ServerHostTrustRules.Evaluate([confirmed], "HOST.example.com", 22, "ssh-ed25519", keyA)
                == ServerHostKeyTrust.Trusted,
            "同端点同算法同密钥判为可信");
        Check(ServerHostTrustRules.Evaluate([confirmed], "host.example.com", 22, "ssh-ed25519", keyB)
                == ServerHostKeyTrust.Changed,
            "同端点同算法密钥变化判为变化");
        Check(ServerHostTrustRules.ProblemCode(ServerHostKeyTrust.Changed)
                == ServerDeploymentProblemCodes.HostKeyChanged,
            "密钥变化对应稳定问题码");
        Check(ServerHostTrustRules.Evaluate([confirmed], "host.example.com", 2222, "ssh-ed25519", keyA)
                == ServerHostKeyTrust.Unknown,
            "不同端口是不同端点");
        Check(ServerHostTrustRules.Evaluate([confirmed], "host.example.com", 22, "rsa-sha2-256", keyA)
                == ServerHostKeyTrust.Unknown,
            "同端点不同算法各自独立确认");

        Check(ServerHostTrustRules.BlocksWriteOperations(ServerHostKeyTrust.Changed), "密钥变化阻断写操作");
        Check(!ServerHostTrustRules.BlocksWriteOperations(ServerHostKeyTrust.Unknown), "未知密钥由用户当次确认而非直接阻断");
        Check(!ServerHostTrustRules.BlocksWriteOperations(ServerHostKeyTrust.Trusted), "可信密钥允许写操作");
        Check(ServerHostTrustRules.ProblemCode(ServerHostKeyTrust.Trusted) is null, "可信状态没有问题码");

        var rotated = confirmed with
        {
            PublicKeyBase64 = Convert.ToBase64String(keyB),
            Fingerprint = ServerHostTrustRules.Fingerprint(keyB),
            ConfirmedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1)
        };
        var replaced = ServerHostTrustRules.Replace([confirmed], rotated);
        Check(replaced.Count == 1, "确认新密钥后同端点同算法只保留一条记录");
        Check(ServerHostTrustRules.Evaluate(replaced, "host.example.com", 22, "ssh-ed25519", keyB)
                == ServerHostKeyTrust.Trusted,
            "替换后的记录立即生效");
    }

    private static void VerifyTunnelResolution()
    {
        Check(ServerTunnelRules.LoopbackHost == "127.0.0.1", "隧道绑定地址固定为 127.0.0.1");
        Check(ServerTunnelRules.IsLoopbackHost("127.0.0.1") && ServerTunnelRules.IsLoopbackHost("127.5.5.5"),
            "整个 127/8 视为回环");
        Check(ServerTunnelRules.IsLoopbackHost("::1"), "IPv6 回环被识别");
        Check(!ServerTunnelRules.IsLoopbackHost("0.0.0.0"), "通配地址不是回环");
        Check(!ServerTunnelRules.IsLoopbackHost("192.168.1.10"), "局域网地址不是回环");
        Check(!ServerTunnelRules.IsLoopbackHost("localhost"), "未解析的 localhost 不被当作回环");
        Check(!ServerTunnelRules.IsAcceptableTunnelBindAddress("0.0.0.0"), "隧道拒绝绑定通配地址");
        Check(ServerTunnelRules.IsAcceptableTunnelBindAddress("127.0.0.1"), "隧道接受回环地址");
        Check(ServerTunnelRules.EphemeralPort == 0, "隧道使用系统分配端口");

        Check(ServerTunnelRules.BuildLoopbackBaseUrl(51000) == "http://127.0.0.1:51000", "回环地址构造正确");
        Check(ServerTunnelRules.BuildLoopbackBaseUrl(51000, "relaxkonos/") == "http://127.0.0.1:51000/relaxkonos",
            "回环地址保留基础路径");
        Check(ServerTunnelRules.TryGetLoopbackPort("http://127.0.0.1:51000") == 51000, "可解析回环端口");
        Check(ServerTunnelRules.TryGetLoopbackPort("http://example.com:51000") is null, "非回环地址不给出端口");
        Check(ServerTunnelRules.TryGetLoopbackPort("http://127.0.0.1") is null, "缺少显式端口时不猜测端口");

        var at = DateTimeOffset.UnixEpoch;
        var installationId = ServerInstallationId.NewId();
        var tunnel = ServerTunnelRules.ManagedTunnel(installationId, 51000, null, at);
        Check(tunnel.Transport == ServerConnectionTransportKind.SshTunnel && tunnel.LocalPort == 51000,
            "受管隧道解析记录传输方式与端口");
        Check(tunnel.Identity.Kind == ServerServiceIdKind.ManagedInstallation
                && tunnel.Identity.ServiceId == installationId,
            "受管隧道身份是安装标识");

        var rebound = ServerTunnelRules.RebindTunnel(tunnel, 52345, at.AddMinutes(1));
        Check(rebound.LocalPort == 52345, "重建隧道更新本地端口");
        Check(rebound.Identity.EffectiveBaseUrl == "http://127.0.0.1:52345", "重建隧道更新传输地址");
        Check(ServerConnectionIdentityRules.PreservesIdentity(tunnel.Identity, rebound.Identity),
            "换端口后登录身份不变");
        Check(rebound.Identity.ServiceId == tunnel.Identity.ServiceId, "换端口后 serviceId 不变");

        var withPath = ServerTunnelRules.RebindTunnel(
            ServerTunnelRules.ManagedTunnel(installationId, 51000, "relaxkonos", at), 52345, at);
        Check(withPath.Identity.EffectiveBaseUrl == "http://127.0.0.1:52345/relaxkonos", "换端口保留基础路径");

        var direct = ServerTunnelRules.Direct("https://example.com/", at);
        Check(direct.Transport == ServerConnectionTransportKind.Direct && direct.LocalPort is null,
            "直连解析不使用隧道");
        Check(direct.Identity.ServiceId == direct.Identity.EffectiveBaseUrl, "直连身份与传输地址一致");

        var rejected = false;
        try { ServerTunnelRules.RebindTunnel(direct, 51000, at); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "直连解析不能被当作隧道重建");
    }

    private static void VerifyProblemCodes()
    {
        var codes = typeof(ServerDeploymentProblemCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Check(codes.Count >= 30, "问题码覆盖请求、信任、预检、包、执行与终态");
        Check(codes.Distinct(StringComparer.Ordinal).Count() == codes.Count, "问题码互不重复");
        Check(codes.All(c => c.StartsWith("server-deployment.", StringComparison.Ordinal)), "问题码带稳定前缀");
    }

    private static void VerifyHostTarget()
    {
        var now = DateTimeOffset.Parse("2026-09-25T06:00:00Z", CultureInfo.InvariantCulture);

        var target = ServerHostTargetRules.Create("Host.Example", 22, "deploy", "  机房 A  ", now);
        Check(target.HostId == ServerHostTargetRules.HostId("host.example", 22, "deploy"), "宿主标识由规范化服务器和用户派生");
        Check(ServerHostTargetRules.IsHostId(target.HostId), "宿主标识形状合法");
        Check(target.SshHost == "host.example", "SSH 主机名被规范化");
        Check(target.DisplayName == "机房 A", "展示名去空白并保留原意");
        Check(target.InstallationId is null && target.LastVerified is null, "新建宿主目标没有安装标识与核验状态");

        // 同一服务器的不同 SSH 用户必须是独立的可选管理目标。
        var again = ServerHostTargetRules.Create("HOST.EXAMPLE", 22, "other", null, now);
        Check(again.HostId != target.HostId, "同端点不同用户是不同宿主目标");
        Check(again.DisplayName == "host.example:22", "缺省展示名回落到端点键而非编造名称");
        Check(ServerHostTargetRules.HostId("host.example", 2222, "deploy") != target.HostId, "不同端口是不同宿主目标");

        // 这两个值是 Kotlin `ServerHostTargetRules.hostId` 的输出。两端必须逐字一致，
        // 否则同一台宿主会在桌面与手机上得到两条不同的管理资料。
        Check(ServerHostTargetRules.HostId("host.example", 22, "deploy") == "rkhost-3191daa32b9b1e91",
            "宿主标识与 Kotlin 派生一致（一）");
        Check(ServerHostTargetRules.HostId("node-1", 2222, "root") == "rkhost-179ee8d9b69573a2",
            "宿主标识与 Kotlin 派生一致（二）");

        Check(!ServerHostTargetRules.IsValidEndpoint("host", 0, "deploy"), "端口 0 被拒绝");
        Check(!ServerHostTargetRules.IsValidEndpoint("host", 70000, "deploy"), "越界端口被拒绝");
        Check(!ServerHostTargetRules.IsValidEndpoint("host", 22, "  "), "空 SSH 用户被拒绝");

        // 安装标识只能由核验结果写入，用户表单不能直接声称。
        var installed = ServerHostTargetRules.ApplyVerifiedState(
            target,
            new ServerHostVerifiedState(
                Installed: true, Mode: ServerInstallMode.LinuxSystem,
                InstallationId: "rki-" + new string('a', 32), Version: "0.1.0",
                ListenUrl: "http://127.0.0.1:5090", Healthy: true, VerifiedAtUtc: now),
            now);
        Check(ServerHostTargetRules.HasManagedInstallation(installed), "核验后获得受管安装标识");
        Check(installed.InstallationId == "rki-" + new string('a', 32), "受管标识被规范化保存");
        Check(ServerHostTargetRules.MatchesInstallation(installed, installed.InstallationId),
            "安装标识一致才可关联登录");
        Check(!ServerHostTargetRules.MatchesInstallation(installed, "rki-" + new string('b', 32)),
            "不同安装标识不关联");
        Check(!ServerHostTargetRules.MatchesInstallation(installed, "http://host.example"),
            "URL 文本不足以关联宿主与登录");

        // 探测失败或宿主上尚无安装时不得清掉已知的受管标识，否则隧道身份会丢失。
        var uninstalled = ServerHostTargetRules.ApplyVerifiedState(
            installed,
            new ServerHostVerifiedState(false, null, null, null, null, false, now.AddMinutes(5)),
            now.AddMinutes(5));
        Check(uninstalled.InstallationId == installed.InstallationId, "未安装的核验不清除已知受管标识");
        Check(uninstalled.LastVerified?.Installed == false, "核验状态仍记录为未安装");

        var invalidId = ServerHostTargetRules.ApplyVerifiedState(
            target,
            new ServerHostVerifiedState(true, ServerInstallMode.LinuxUser, "not-an-installation-id", "0.1.0", null, true, now),
            now);
        Check(invalidId.InstallationId is null, "非法安装标识不被采纳");

        Check(ServerHostTargetRules.Status(installed, ServerHostKeyTrust.Changed) == ServerHostTargetStatus.HostKeyChanged,
            "密钥变化优先于其他状态");
        Check(ServerHostTargetRules.Status(installed, ServerHostKeyTrust.Unknown) == ServerHostTargetStatus.HostKeyUnknown,
            "未知密钥要求先核对指纹");
        Check(ServerHostTargetRules.Status(target, ServerHostKeyTrust.Trusted) == ServerHostTargetStatus.Unverified,
            "未核验宿主报告为未核验");
        Check(ServerHostTargetRules.Status(uninstalled, ServerHostKeyTrust.Trusted) == ServerHostTargetStatus.ReachableNotInstalled,
            "SSH 可达但未安装");
        Check(ServerHostTargetRules.Status(installed, ServerHostKeyTrust.Trusted) == ServerHostTargetStatus.InstalledHealthy,
            "已安装且健康");
        Check(
            ServerHostTargetRules.Status(installed with { LastVerified = installed.LastVerified! with { Healthy = false } },
                ServerHostKeyTrust.Trusted) == ServerHostTargetStatus.ServiceUnhealthy,
            "已安装但服务异常");

        Check(ServerHostTargetRules.VerifiedLabel(target) is null, "从未核验时不伪造核验时间");
        Check(ServerHostTargetRules.VerifiedLabel(installed) is not null, "核验后给出可展示的核验时间");

        // 快照投影必须逐字段搬运，不得在客户端重新判断宿主状态。
        var snapshot = new ServerHostSnapshotDto(
            InstallationId: "rki-" + new string('c', 32), Installed: true, Mode: ServerInstallMode.WindowsSystem,
            Version: "0.2.0", PreviousVersion: null, InstallRoot: null, DataRoot: null,
            ListenUrl: "http://127.0.0.1:5090", Healthy: true, DataRetained: null, ServiceNames: null,
            VerifiedAtUtc: now);
        var projected = ServerHostTargetRules.VerifiedStateFrom(snapshot);
        Check(projected.Mode == ServerInstallMode.WindowsSystem && projected.Version == "0.2.0"
              && projected.InstallationId == snapshot.InstallationId && projected.Healthy,
            "快照投影逐字段一致");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
