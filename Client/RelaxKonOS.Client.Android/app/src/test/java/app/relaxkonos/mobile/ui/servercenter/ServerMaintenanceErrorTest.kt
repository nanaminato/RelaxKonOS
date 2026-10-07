package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.servercenter.ServerDeploymentProblemCodes
import com.jcraft.jsch.JSchException
import java.net.SocketTimeoutException
import org.junit.Assert.*
import org.junit.Test

class ServerMaintenanceErrorTest {
    @Test fun unexpectedReadFailureNeverContainsRawCommandOrCredentials() {
        val message = maintenanceErrorMessage(IllegalStateException("endpoint private-password /secret/path"), false)
        assertEquals(R.string.server_maintenance_read_failed, message.resId)
        assertTrue(message.args.isEmpty())
        assertNull(message.debugDetail)
    }
    @Test fun preSubmissionTimeoutKeepsTransportClassification() {
        assertEquals(R.string.server_center_ssh_failed_timeout,
            maintenanceErrorMessage(JSchException("connect private endpoint", SocketTimeoutException()), false).resId)
    }
    @Test fun lostMutationReceiptRequiresVerificationInsteadOfBlindRetry() {
        assertEquals(R.string.server_maintenance_result_unknown,
            maintenanceErrorMessage(SocketTimeoutException("private endpoint"), true).resId)
    }
    @Test fun authoritativeFailurePreservesOnlyKnownProblemCode() {
        val message = maintenanceErrorMessage(IllegalStateException(ServerDeploymentProblemCodes.DISK_FULL), true)
        assertEquals(R.string.server_maintenance_failed_code, message.resId)
        assertEquals(listOf(ServerDeploymentProblemCodes.DISK_FULL), message.args)
    }
    @Test fun failedPostconditionDoesNotClaimSuccess() {
        assertEquals(R.string.server_maintenance_verification_failed,
            maintenanceErrorMessage(IllegalStateException("server-deployment.postcondition_failed"), true).resId)
    }
}
