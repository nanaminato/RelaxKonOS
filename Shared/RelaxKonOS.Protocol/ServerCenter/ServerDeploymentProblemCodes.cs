namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 稳定的部署问题码。客户端据此本地化文案；原始命令输出、宿主路径细节与凭据永远不作为产品文案。
/// 每个码都对应一个可执行的下一步，而不是笼统的“失败了”。
/// </summary>
public static class ServerDeploymentProblemCodes
{
    // 请求与契约校验。
    public const string InvalidRequest = "server-deployment.invalid_request";
    public const string UnsupportedProtocolVersion = "server-deployment.unsupported_protocol_version";
    public const string UnknownAction = "server-deployment.unknown_action";
    public const string PathNotAllowed = "server-deployment.path_not_allowed";
    public const string ConfirmationRequired = "server-deployment.confirmation_required";
    public const string IdempotencyRequired = "server-deployment.idempotency_required";
    public const string IdempotencyConflict = "server-deployment.idempotency_conflict";
    public const string WriteLockHeld = "server-deployment.write_lock_held";
    public const string OperationNotFound = "server-deployment.operation_not_found";
    public const string NotCancellable = "server-deployment.not_cancellable";
    public const string NotSupported = "server-deployment.not_supported";

    // SSH 信任与认证。
    public const string HostKeyUnknown = "server-deployment.host_key_unknown";
    public const string HostKeyChanged = "server-deployment.host_key_changed";
    public const string SshUnreachable = "server-deployment.ssh_unreachable";
    public const string AuthenticationFailed = "server-deployment.authentication_failed";
    public const string PrivilegeDenied = "server-deployment.privilege_denied";
    public const string ElevationRequired = "server-deployment.elevation_required";

    // 宿主与权限预检。
    public const string NotInstalled = "server-deployment.not_installed";
    public const string AlreadyInstalled = "server-deployment.already_installed";
    public const string InstallationIdMismatch = "server-deployment.installation_id_mismatch";
    public const string OsUnsupported = "server-deployment.os_unsupported";
    public const string ArchitectureMismatch = "server-deployment.architecture_mismatch";
    public const string DiskFull = "server-deployment.disk_full";
    public const string PortUnavailable = "server-deployment.port_unavailable";
    public const string DependencyMissing = "server-deployment.dependency_missing";

    // 包与发布信任。
    public const string PackageUnavailable = "server-deployment.package_unavailable";
    public const string PackageSignatureInvalid = "server-deployment.package_signature_invalid";
    public const string PackageTrustRootMissing = "server-deployment.package_trust_root_missing";
    public const string PackageDigestMismatch = "server-deployment.package_digest_mismatch";
    public const string PackageManifestInvalid = "server-deployment.package_manifest_invalid";
    public const string PackageRuntimeMismatch = "server-deployment.package_runtime_mismatch";
    public const string PackageLayoutUnsafe = "server-deployment.package_layout_unsafe";

    // 执行与健康。
    public const string ServiceStopFailed = "server-deployment.service_stop_failed";
    public const string ServiceStartFailed = "server-deployment.service_start_failed";
    public const string ActivationFailed = "server-deployment.activation_failed";
    public const string HealthCheckFailed = "server-deployment.health_check_failed";
    public const string RollbackFailed = "server-deployment.rollback_failed";
    public const string MigrationIrreversible = "server-deployment.migration_irreversible";
    public const string SnapshotFailed = "server-deployment.snapshot_failed";

    // 卸载范围与完成证明。
    public const string UninstallScopeViolation = "server-deployment.uninstall_scope_violation";
    public const string UninstallIncomplete = "server-deployment.uninstall_incomplete";

    // 终态。
    public const string Failed = "server-deployment.failed";
    public const string Cancelled = "server-deployment.cancelled";
    public const string Interrupted = "server-deployment.interrupted";
    public const string RecoveryUnknown = "server-deployment.recovery_unknown";
}