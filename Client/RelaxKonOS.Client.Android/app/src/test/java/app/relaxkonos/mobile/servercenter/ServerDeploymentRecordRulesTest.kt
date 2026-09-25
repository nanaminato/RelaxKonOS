package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** 与 C# 回执读取器一致：断线恢复只信任同一操作的完整宿主核验结果。 */
class ServerDeploymentRecordRulesTest {
    private val operationId = "11111111-1111-1111-1111-111111111111"
    private val installationId = "rki-" + "a".repeat(32)

    private val completed = ServerDeploymentOperation(
        schemaVersion = ServerDeploymentProtocol.VERSION,
        operationId = operationId,
        installationId = installationId,
        kind = ServerDeploymentKind.Install,
        phase = ServerDeploymentPhase.Completed,
        state = ServerDeploymentState.Succeeded,
        sequence = 4,
        timestampUtc = "2026-09-25T07:48:21Z",
        cancellable = false,
        progress = 100,
        result = ServerDeploymentResult(
            installationId = installationId,
            mode = ServerInstallMode.LinuxUser,
            version = "0.1.0",
            healthy = true,
        ),
    )

    @Test
    fun `successful install needs matching id and verified health`() {
        assertNull(ServerDeploymentRecordRules.validateRecord(completed, operationId))
        assertEquals(ServerDeploymentProblemCodes.INVALID_REQUEST,
            ServerDeploymentRecordRules.validateRecord(completed, "22222222-2222-2222-2222-222222222222"))
        assertEquals(ServerDeploymentProblemCodes.INVALID_REQUEST,
            ServerDeploymentRecordRules.validateRecord(
                completed.copy(result = completed.result!!.copy(healthy = false)), operationId))
    }

    @Test
    fun `terminal receipt cannot advertise cancellation`() {
        assertEquals(ServerDeploymentProblemCodes.INVALID_REQUEST,
            ServerDeploymentRecordRules.validateRecord(completed.copy(cancellable = true), operationId))
    }

    @Test
    fun `successful probe needs host facts`() {
        assertEquals(ServerDeploymentProblemCodes.INVALID_REQUEST,
            ServerDeploymentRecordRules.validateRecord(
                completed.copy(kind = ServerDeploymentKind.Probe, result = null, probe = null), operationId))
    }

    @Test
    fun `event sequence and phases must advance`() {
        val first = ServerDeploymentEvent(
            ServerDeploymentProtocol.VERSION, operationId, installationId,
            ServerDeploymentKind.Install, ServerDeploymentPhase.Preflight,
            ServerDeploymentState.Running, 1, "2026-09-25T07:48:21Z")
        val second = first.copy(phase = ServerDeploymentPhase.HealthChecking, sequence = 2)
        assertNull(ServerDeploymentRecordRules.validateEvent(first, operationId))
        assertNull(ServerDeploymentRecordRules.validateEvent(second, operationId, first))
        assertEquals(ServerDeploymentProblemCodes.INVALID_REQUEST,
            ServerDeploymentRecordRules.validateEvent(first.copy(sequence = 3), operationId, second))
        assertEquals(ServerDeploymentProblemCodes.INVALID_REQUEST,
            ServerDeploymentRecordRules.validateEvent(
                second.copy(kind = ServerDeploymentKind.Uninstall, sequence = 3), operationId, second))
    }
}
