package app.relaxkonos.mobile.ui.servercenter

import app.relaxkonos.mobile.core.auth.SavedCredentialState
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ServerCenterSubmissionTest {
    private val managing = ServerCenterUiState(
        hosts = emptyList(), formMode = ServerCenterFormMode.Manage, selectedHostId = "host",
    )

    @Test fun `verified session allows management without a saved or typed password`() {
        assertFalse(managing.canSubmit)
        assertTrue(managing.copy(hasSessionPassword = true).canSubmit)
        assertFalse(managing.copy(hasSessionPassword = true, isVerifying = true).canSubmit)
    }

    @Test fun `manual correction remains available when saved credential is unusable`() {
        for (credential in listOf(SavedCredentialState.Absent, SavedCredentialState.Unavailable, SavedCredentialState.Invalidated)) {
            val state = managing.copy(credentialStates = mapOf("host" to credential))
            assertFalse(state.canSubmit)
            assertTrue(state.copy(password = "corrected").canSubmit)
            assertTrue(state.copy(hasSessionPassword = true).canSubmit)
        }
    }

    @Test fun `available vault allows submission but authorization still happens on connect`() {
        val state = managing.copy(credentialStates = mapOf("host" to SavedCredentialState.Available))
        assertTrue(state.canSubmit)
        assertFalse(state.copy(isVerifying = true).canSubmit)
    }

    @Test fun `session from a prior host cannot authorize adding or managing without a target`() {
        assertFalse(managing.copy(selectedHostId = null, hasSessionPassword = true).canSubmit)
        val adding = managing.copy(formMode = ServerCenterFormMode.Add, hasSessionPassword = true)
        assertFalse(adding.canSubmit)
        assertTrue(adding.copy(password = "new password").canSubmit)
        assertFalse(adding.copy(password = "new password", isVerifying = true).canSubmit)
    }
}
