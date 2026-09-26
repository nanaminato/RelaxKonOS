package app.relaxkonos.mobile.servercenter

/**
 * 稳定的部署问题码。与 C# `ServerDeploymentProblemCodes` 逐字一致；客户端据此本地化文案，
 * 原始命令输出与凭据永远不作为产品文案。
 */
object ServerDeploymentProblemCodes {
    // 请求与契约校验。
    const val INVALID_REQUEST = "server-deployment.invalid_request"
    const val UNSUPPORTED_PROTOCOL_VERSION = "server-deployment.unsupported_protocol_version"
    const val UNKNOWN_ACTION = "server-deployment.unknown_action"
    const val PATH_NOT_ALLOWED = "server-deployment.path_not_allowed"
    const val CONFIRMATION_REQUIRED = "server-deployment.confirmation_required"
    const val IDEMPOTENCY_REQUIRED = "server-deployment.idempotency_required"
    const val IDEMPOTENCY_CONFLICT = "server-deployment.idempotency_conflict"
    const val WRITE_LOCK_HELD = "server-deployment.write_lock_held"
    const val OPERATION_NOT_FOUND = "server-deployment.operation_not_found"
    const val NOT_CANCELLABLE = "server-deployment.not_cancellable"
    const val NOT_SUPPORTED = "server-deployment.not_supported"

    // SSH 信任与认证。
    const val HOST_KEY_UNKNOWN = "server-deployment.host_key_unknown"
    const val HOST_KEY_CHANGED = "server-deployment.host_key_changed"
    const val SSH_UNREACHABLE = "server-deployment.ssh_unreachable"
    const val AUTHENTICATION_FAILED = "server-deployment.authentication_failed"
    const val PRIVILEGE_DENIED = "server-deployment.privilege_denied"
    const val ELEVATION_REQUIRED = "server-deployment.elevation_required"

    // 宿主与权限预检。
    const val NOT_INSTALLED = "server-deployment.not_installed"
    const val ALREADY_INSTALLED = "server-deployment.already_installed"
    const val INSTALLATION_ID_MISMATCH = "server-deployment.installation_id_mismatch"
    const val OS_UNSUPPORTED = "server-deployment.os_unsupported"
    const val ARCHITECTURE_MISMATCH = "server-deployment.architecture_mismatch"
    const val DISK_FULL = "server-deployment.disk_full"
    const val PORT_UNAVAILABLE = "server-deployment.port_unavailable"
    const val DEPENDENCY_MISSING = "server-deployment.dependency_missing"

    // 包与发布信任。
    const val PACKAGE_UNAVAILABLE = "server-deployment.package_unavailable"
    const val PACKAGE_SIGNATURE_INVALID = "server-deployment.package_signature_invalid"
    const val PACKAGE_TRUST_ROOT_MISSING = "server-deployment.package_trust_root_missing"
    const val PACKAGE_DIGEST_MISMATCH = "server-deployment.package_digest_mismatch"
    const val PACKAGE_MANIFEST_INVALID = "server-deployment.package_manifest_invalid"
    const val PACKAGE_RUNTIME_MISMATCH = "server-deployment.package_runtime_mismatch"
    const val PACKAGE_LAYOUT_UNSAFE = "server-deployment.package_layout_unsafe"

    // 执行与健康。
    const val SERVICE_STOP_FAILED = "server-deployment.service_stop_failed"
    const val SERVICE_START_FAILED = "server-deployment.service_start_failed"
    const val ACTIVATION_FAILED = "server-deployment.activation_failed"
    const val HEALTH_CHECK_FAILED = "server-deployment.health_check_failed"
    const val ROLLBACK_FAILED = "server-deployment.rollback_failed"
    const val MIGRATION_IRREVERSIBLE = "server-deployment.migration_irreversible"
    const val SNAPSHOT_FAILED = "server-deployment.snapshot_failed"

    // 卸载范围与完成证明。
    const val UNINSTALL_SCOPE_VIOLATION = "server-deployment.uninstall_scope_violation"
    const val UNINSTALL_INCOMPLETE = "server-deployment.uninstall_incomplete"

    // 终态。
    const val FAILED = "server-deployment.failed"
    const val CANCELLED = "server-deployment.cancelled"
    const val INTERRUPTED = "server-deployment.interrupted"
    const val RECOVERY_UNKNOWN = "server-deployment.recovery_unknown"

    /** 全部问题码，供测试与本地化覆盖检查使用。 */
    val all: List<String> = listOf(
        INVALID_REQUEST, UNSUPPORTED_PROTOCOL_VERSION, UNKNOWN_ACTION, PATH_NOT_ALLOWED,
        CONFIRMATION_REQUIRED, IDEMPOTENCY_REQUIRED, IDEMPOTENCY_CONFLICT, WRITE_LOCK_HELD,
        OPERATION_NOT_FOUND, NOT_CANCELLABLE, NOT_SUPPORTED,
        HOST_KEY_UNKNOWN, HOST_KEY_CHANGED, SSH_UNREACHABLE, AUTHENTICATION_FAILED,
        PRIVILEGE_DENIED, ELEVATION_REQUIRED,
        NOT_INSTALLED, ALREADY_INSTALLED, INSTALLATION_ID_MISMATCH, OS_UNSUPPORTED,
        ARCHITECTURE_MISMATCH, DISK_FULL, PORT_UNAVAILABLE, DEPENDENCY_MISSING,
        PACKAGE_UNAVAILABLE, PACKAGE_SIGNATURE_INVALID, PACKAGE_TRUST_ROOT_MISSING,
        PACKAGE_DIGEST_MISMATCH, PACKAGE_MANIFEST_INVALID, PACKAGE_RUNTIME_MISMATCH,
        PACKAGE_LAYOUT_UNSAFE,
        SERVICE_STOP_FAILED, SERVICE_START_FAILED, ACTIVATION_FAILED, HEALTH_CHECK_FAILED,
        ROLLBACK_FAILED, MIGRATION_IRREVERSIBLE, SNAPSHOT_FAILED,
        UNINSTALL_SCOPE_VIOLATION, UNINSTALL_INCOMPLETE,
        FAILED, CANCELLED, INTERRUPTED, RECOVERY_UNKNOWN,
    )
}

/**
 * 安装标识：首次安装时由部署引擎签发并写入安装清单，此后作为受管隧道的稳定身份。
 * 规范形式为 `rki-` 加 32 位小写十六进制，与端口、地址无关。
 */
object ServerInstallationId {
    const val PREFIX = "rki-"
    private const val HEX_LENGTH = 32

    fun isHexLowercase(value: String): Boolean =
        value.length == HEX_LENGTH && value.all { it in '0'..'9' || it in 'a'..'f' }

    fun isValid(value: String?): Boolean {
        if (value.isNullOrBlank()) return false
        val trimmed = value.trim()
        if (!trimmed.startsWith(PREFIX)) return false
        return isHexLowercase(trimmed.substring(PREFIX.length))
    }
}

/** 暂存包名、摘要与版本号的受限校验，与 C# `ServerDeploymentInputRules` 对齐。 */
object ServerDeploymentInputRules {
    private const val MAX_STAGED_NAME_LENGTH = 128

    /** 暂存目录内的裸文件名：只允许字母、数字、点、下划线、连字符，必须以 .zip 结尾。 */
    fun isSafeStagedPackageName(name: String?): Boolean {
        if (name.isNullOrBlank() || name.length > MAX_STAGED_NAME_LENGTH) return false
        if (name.contains('/') || name.contains('\\') || name.contains("..")) return false
        if (!name.endsWith(".zip", ignoreCase = true)) return false
        return name.all { it.isLetterOrDigit() && it.code < 128 || it == '.' || it == '_' || it == '-' }
    }

    /** SHA-256 十六进制摘要（64 位十六进制，大小写不敏感）。 */
    fun isSha256(value: String?): Boolean =
        value != null && value.length == 64 && value.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }

    /** 版本字符串：点分数字段加可选预发布后缀。 */
    fun isVersion(value: String?): Boolean {
        if (value.isNullOrBlank() || value.length > 64) return false
        if (!value.any { it in '0'..'9' }) return false
        return value.all { it.isLetterOrDigit() && it.code < 128 || it == '.' || it == '-' || it == '+' }
    }
}

/**
 * 部署操作的生命周期不变量。阶段序号单调，取消只在安全点生效。
 */
object ServerDeploymentLifecycle {
    fun isTerminalState(state: ServerDeploymentState): Boolean = when (state) {
        ServerDeploymentState.Succeeded, ServerDeploymentState.Failed,
        ServerDeploymentState.Cancelled, ServerDeploymentState.Interrupted -> true
        else -> false
    }

    fun isTerminalPhase(phase: ServerDeploymentPhase): Boolean = when (phase) {
        ServerDeploymentPhase.Completed, ServerDeploymentPhase.Failed,
        ServerDeploymentPhase.Cancelled, ServerDeploymentPhase.Interrupted -> true
        else -> false
    }

    /** 可在安全点取消的阶段：校验、取锁、预检、暂存、传输、校验包、快照。 */
    fun isCancellablePhase(phase: ServerDeploymentPhase): Boolean = when (phase) {
        ServerDeploymentPhase.Queued, ServerDeploymentPhase.ValidatingRequest,
        ServerDeploymentPhase.AcquiringLock, ServerDeploymentPhase.Preflight,
        ServerDeploymentPhase.Staging, ServerDeploymentPhase.Transferring,
        ServerDeploymentPhase.VerifyingPackage, ServerDeploymentPhase.Snapshotting -> true
        else -> false
    }

    /** 临界区：从停止服务到回滚完成。此区间不得强杀进程，界面显示“正在完成安全恢复”。 */
    fun isSafeRecoveryWindow(phase: ServerDeploymentPhase): Boolean = when (phase) {
        ServerDeploymentPhase.Stopping, ServerDeploymentPhase.Activating,
        ServerDeploymentPhase.Starting, ServerDeploymentPhase.HealthChecking,
        ServerDeploymentPhase.RollingBack, ServerDeploymentPhase.Finalizing -> true
        else -> false
    }

    fun isMonotonicPhase(previous: ServerDeploymentPhase, next: ServerDeploymentPhase): Boolean =
        next.ordinal >= previous.ordinal

    fun isMonotonicSequence(previousSequence: Long, nextSequence: Long): Boolean =
        nextSequence > previousSequence

    /** 校验一次事件推进是否合法。返回 null 表示合法，否则返回稳定的问题码。 */
    fun validateEventProgress(
        previousPhase: ServerDeploymentPhase,
        previousSequence: Long,
        nextPhase: ServerDeploymentPhase,
        nextSequence: Long,
    ): String? {
        if (!isMonotonicSequence(previousSequence, nextSequence)) return ServerDeploymentProblemCodes.INVALID_REQUEST
        if (!isMonotonicPhase(previousPhase, nextPhase)) return ServerDeploymentProblemCodes.INVALID_REQUEST
        return null
    }

    /** 操作进入终态后 Cancellable 必须为 false。 */
    fun isCancellable(phase: ServerDeploymentPhase, state: ServerDeploymentState): Boolean =
        !isTerminalState(state) && isCancellablePhase(phase)
}

/**
 * 远端持久回执与事件推进的校验。客户端恢复操作时只把通过这些检查的回执当成权威状态；
 * SSH 命令退出码和最后一帧事件都不足以证明安装完成。
 */
object ServerDeploymentRecordRules {
    fun validateRecord(record: ServerDeploymentOperation, expectedOperationId: String): String? {
        if (record.schemaVersion != ServerDeploymentProtocol.VERSION ||
            record.operationId != expectedOperationId || record.sequence < 0 ||
            record.timestampUtc.isBlank() || record.progress?.let { it !in 0..100 } == true ||
            record.installationId?.let { !ServerInstallationId.isValid(it) } == true ||
            record.cancellable != ServerDeploymentLifecycle.isCancellable(record.phase, record.state)
        ) return ServerDeploymentProblemCodes.INVALID_REQUEST

        if (record.state == ServerDeploymentState.Succeeded) {
            if (record.phase != ServerDeploymentPhase.Completed) return ServerDeploymentProblemCodes.INVALID_REQUEST
            when (record.kind) {
                ServerDeploymentKind.Install, ServerDeploymentKind.Upgrade,
                ServerDeploymentKind.Repair, ServerDeploymentKind.Rollback -> {
                    val result = record.result ?: return ServerDeploymentProblemCodes.INVALID_REQUEST
                    if (!result.healthy || !ServerInstallationId.isValid(result.installationId) ||
                        !ServerDeploymentInputRules.isVersion(result.version)
                    ) return ServerDeploymentProblemCodes.INVALID_REQUEST
                }
                ServerDeploymentKind.Uninstall ->
                    if (record.result?.dataRetained == null) return ServerDeploymentProblemCodes.INVALID_REQUEST
                ServerDeploymentKind.Probe ->
                    if (record.probe == null) return ServerDeploymentProblemCodes.INVALID_REQUEST
                ServerDeploymentKind.Status ->
                    if (record.snapshot == null) return ServerDeploymentProblemCodes.INVALID_REQUEST
            }
        }
        return null
    }

    fun validateEvent(
        event: ServerDeploymentEvent,
        expectedOperationId: String,
        previous: ServerDeploymentEvent? = null,
    ): String? {
        if (event.schemaVersion != ServerDeploymentProtocol.VERSION ||
            event.operationId != expectedOperationId || event.sequence <= 0 ||
            event.timestampUtc.isBlank() || event.progress?.let { it !in 0..100 } == true ||
            event.installationId?.let { !ServerInstallationId.isValid(it) } == true
        ) return ServerDeploymentProblemCodes.INVALID_REQUEST
        if (previous != null && ServerDeploymentLifecycle.validateEventProgress(
                previous.phase, previous.sequence, event.phase, event.sequence,
            ) != null
        ) return ServerDeploymentProblemCodes.INVALID_REQUEST
        if (previous != null && (ServerDeploymentLifecycle.isTerminalState(previous.state) ||
            previous.kind != event.kind ||
            previous.installationId != null && previous.installationId != event.installationId)
        ) return ServerDeploymentProblemCodes.INVALID_REQUEST
        return null
    }
}

/**
 * 卸载数据处置策略。默认保留数据；删除数据是显式、需二次确认、不可恢复的动作。
 */
object ServerDataRetentionPolicy {
    val DEFAULT_UNINSTALL = ServerDataRetention.Retain

    /** 保留数据时被留下的范围。重新安装必须能识别并复用它们。 */
    val retainedScopes: List<ServerDataScope> = listOf(
        ServerDataScope.Configuration, ServerDataScope.Database, ServerDataScope.Secrets,
        ServerDataScope.Logs, ServerDataScope.State,
    )

    /** 删除数据时被移除的范围，也就是确认页必须逐项列出的不可恢复内容。 */
    val irrecoverableScopes: List<ServerDataScope> = listOf(
        ServerDataScope.Database, ServerDataScope.Secrets, ServerDataScope.Configuration,
        ServerDataScope.Logs, ServerDataScope.State,
    )

    fun deletedScopes(retention: ServerDataRetention): List<ServerDataScope> =
        if (retention == ServerDataRetention.Delete) irrecoverableScopes else emptyList()

    fun requiresServerNameConfirmation(retention: ServerDataRetention): Boolean =
        retention == ServerDataRetention.Delete

    fun offersExportBeforeDelete(retention: ServerDataRetention): Boolean =
        retention == ServerDataRetention.Delete
}

/** 三种模式的能力矩阵权威定义，与 C# `ServerDeploymentModeMatrix` 对齐。 */
object ServerDeploymentModeMatrix {
    val linuxSystem = ServerDeploymentModeCapabilities(
        mode = ServerInstallMode.LinuxSystem,
        hostPlatform = "linux",
        requiresElevation = true,
        supportsSudoElevation = true,
        requiresElevatedSshToken = false,
        supportsSystemService = true,
        supportsRollback = true,
        supportsDataRetention = true,
        defaultLoopbackOnly = true,
        defaultInstallRootToken = "/opt/relaxkonos",
        defaultDataRootToken = "/var/lib/relaxkonos",
        healthPath = "/healthz",
        serviceNames = listOf("relaxkonos-server.service", "relaxkonos-guardian.service"),
    )

    val linuxUser = ServerDeploymentModeCapabilities(
        mode = ServerInstallMode.LinuxUser,
        hostPlatform = "linux",
        requiresElevation = false,
        supportsSudoElevation = false,
        requiresElevatedSshToken = false,
        supportsSystemService = false,
        supportsRollback = true,
        supportsDataRetention = true,
        defaultLoopbackOnly = true,
        defaultInstallRootToken = "xdg-data/relaxkonos",
        defaultDataRootToken = "xdg-data/relaxkonos",
        healthPath = "/ready",
        serviceNames = emptyList(),
    )

    val windowsSystem = ServerDeploymentModeCapabilities(
        mode = ServerInstallMode.WindowsSystem,
        hostPlatform = "windows",
        requiresElevation = true,
        supportsSudoElevation = false,
        requiresElevatedSshToken = true,
        supportsSystemService = true,
        supportsRollback = true,
        supportsDataRetention = true,
        defaultLoopbackOnly = true,
        defaultInstallRootToken = "%ProgramFiles%\\RelaxKonOS",
        defaultDataRootToken = "%ProgramData%\\RelaxKonOS",
        healthPath = "/healthz",
        serviceNames = listOf("RelaxKonOSServer", "RelaxKonOSGuardian", "RelaxKonOSPrivilegedHelper"),
    )

    val all: List<ServerDeploymentModeCapabilities> = listOf(linuxSystem, linuxUser, windowsSystem)

    fun forMode(mode: ServerInstallMode): ServerDeploymentModeCapabilities = when (mode) {
        ServerInstallMode.LinuxSystem -> linuxSystem
        ServerInstallMode.LinuxUser -> linuxUser
        ServerInstallMode.WindowsSystem -> windowsSystem
    }
}
