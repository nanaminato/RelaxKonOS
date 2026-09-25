namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 部署操作的生命周期不变量。启动器必须满足它们，客户端据此信任进度并决定何时允许取消。
/// <para>阶段序号单调：同一操作内 <see cref="ServerDeploymentPhase"/> 的序号只增不减，
/// <see cref="ServerDeploymentEventDto.Sequence"/> 严格递增。</para>
/// <para>取消只在安全点生效：停服务到回滚完成的临界阶段必须显示“正在完成安全恢复”，不强杀进程。</para>
/// </summary>
public static class ServerDeploymentLifecycle
{
    /// <summary>阶段序号。用于校验单调性；与展示顺序无关。</summary>
    public static int Ordinal(ServerDeploymentPhase phase) => (int)phase;

    public static bool IsTerminalState(ServerDeploymentState state) =>
        state is ServerDeploymentState.Succeeded or ServerDeploymentState.Failed
            or ServerDeploymentState.Cancelled or ServerDeploymentState.Interrupted;

    public static bool IsTerminalPhase(ServerDeploymentPhase phase) =>
        phase is ServerDeploymentPhase.Completed or ServerDeploymentPhase.Failed
            or ServerDeploymentPhase.Cancelled or ServerDeploymentPhase.Interrupted;

    /// <summary>可在安全点取消的阶段：校验、取锁、预检、暂存、传输、校验包、快照。</summary>
    public static bool IsCancellablePhase(ServerDeploymentPhase phase) => phase switch
    {
        ServerDeploymentPhase.Queued => true,
        ServerDeploymentPhase.ValidatingRequest => true,
        ServerDeploymentPhase.AcquiringLock => true,
        ServerDeploymentPhase.Preflight => true,
        ServerDeploymentPhase.Staging => true,
        ServerDeploymentPhase.Transferring => true,
        ServerDeploymentPhase.VerifyingPackage => true,
        ServerDeploymentPhase.Snapshotting => true,
        _ => false
    };

    /// <summary>
    /// 临界区：从停止服务到回滚完成的阶段。此区间不得强杀进程，界面必须显示“正在完成安全恢复”。
    /// </summary>
    public static bool IsSafeRecoveryWindow(ServerDeploymentPhase phase) => phase switch
    {
        ServerDeploymentPhase.Stopping => true,
        ServerDeploymentPhase.Activating => true,
        ServerDeploymentPhase.Starting => true,
        ServerDeploymentPhase.HealthChecking => true,
        ServerDeploymentPhase.RollingBack => true,
        ServerDeploymentPhase.Finalizing => true,
        _ => false
    };

    public static bool IsMonotonicPhase(ServerDeploymentPhase previous, ServerDeploymentPhase next) =>
        Ordinal(next) >= Ordinal(previous);

    public static bool IsMonotonicSequence(long previousSequence, long nextSequence) =>
        nextSequence > previousSequence;

    /// <summary>
    /// 校验一次事件推进是否合法。返回 null 表示合法，否则返回稳定的问题码，便于启动器与客户端共用同一判定。
    /// </summary>
    public static string? ValidateEventProgress(
        ServerDeploymentPhase previousPhase, long previousSequence,
        ServerDeploymentPhase nextPhase, long nextSequence)
    {
        if (!IsMonotonicSequence(previousSequence, nextSequence))
            return ServerDeploymentProblemCodes.InvalidRequest;
        if (!IsMonotonicPhase(previousPhase, nextPhase))
            return ServerDeploymentProblemCodes.InvalidRequest;
        return null;
    }

    /// <summary>操作进入终态后，<c>Cancellable</c> 必须为 false。</summary>
    public static bool IsCancellable(ServerDeploymentPhase phase, ServerDeploymentState state) =>
        !IsTerminalState(state) && IsCancellablePhase(phase);
}