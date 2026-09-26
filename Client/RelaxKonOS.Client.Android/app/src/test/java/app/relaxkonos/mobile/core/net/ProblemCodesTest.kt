package app.relaxkonos.mobile.core.net

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.executionEligibilityMessage
import app.relaxkonos.mobile.ui.common.problemMessage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Problem handling, asserted because the distinction between a `Problem` and a `Transport` decides two
 * things: whether a stored credential is deleted (`RelaxKonOS.Mobile.V1.Design.md` §5.8.2), and what the
 * UI says about a failure. Reading a named 5xx code as a transport failure is what turned the server's
 * "the sampler has no sample yet" into "cannot reach the server".
 */
class ProblemCodesTest {
    @Test
    fun `the code is read from the type uri`() {
        assertEquals(
            ProblemCodes.INVALID_CREDENTIAL,
            ProblemCodes.from("https://relaxkonos.app/problems/invalid-credential", null),
        )
    }

    @Test
    fun `the problemCode extension wins over the type uri`() {
        assertEquals(
            ProblemCodes.ELEVATION_REQUIRED,
            ProblemCodes.from("https://relaxkonos.app/problems/whatever", ProblemCodes.ELEVATION_REQUIRED),
        )
    }

    @Test
    fun `a foreign type uri falls back to its last segment`() {
        assertEquals("something-else", ProblemCodes.from("https://example.test/errors/something-else", null))
    }

    @Test
    fun `a missing code becomes an empty string`() {
        assertEquals("", ProblemCodes.from(null, null))
        assertEquals("", ProblemCodes.from("", ""))
    }

    @Test
    fun `an empty problemCode does not shadow the type uri`() {
        assertEquals(
            ProblemCodes.ELEVATION_REQUIRED,
            ProblemCodes.from("https://relaxkonos.app/problems/elevation-required", "  "),
        )
    }

    @Test
    fun `credential rejection codes are recognised`() {
        assertTrue(ApiResult.Problem(403, ProblemCodes.ELEVATION_PASSWORD_INVALID, null).isCredentialRejection())
        assertTrue(ApiResult.Problem(403, ProblemCodes.ELEVATION_ACCOUNT_NOT_ADMINISTRATOR, null).isCredentialRejection())
        assertTrue(ApiResult.Problem(401, ProblemCodes.INVALID_CREDENTIAL, null).isCredentialRejection())
    }

    @Test
    fun `codes that are not about the submitted credential are not rejections`() {
        assertFalse(ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null).isCredentialRejection())
        assertFalse(ApiResult.Problem(500, "internal-error", null).isCredentialRejection())
        assertFalse(ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null).isCredentialRejection())
    }

    @Test
    fun `only a 401 is a session expiry`() {
        assertTrue(ApiResult.Problem(401, ProblemCodes.UNAUTHORIZED, null).isSessionExpired())
        assertFalse(ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null).isSessionExpired())
    }

    @Test
    fun `the server names a code only through the extension or its own namespace`() {
        assertTrue(ProblemCodes.namesContractCode("https://relaxkonos.app/problems/performance-not-ready", null))
        assertTrue(ProblemCodes.namesContractCode(null, ProblemCodes.PERFORMANCE_NOT_READY))
        assertFalse(ProblemCodes.namesContractCode("https://tools.ietf.org/html/rfc9110#section-15.6.1", null))
        assertFalse(ProblemCodes.namesContractCode(null, null))
        assertFalse(ProblemCodes.namesContractCode("", "  "))
    }

    @Test
    fun `every 4xx is a problem, named or not`() {
        assertTrue(readsAsProblem(403, "https://relaxkonos.app/problems/elevation-required", null))
        assertTrue(readsAsProblem(404, null, null))
    }

    @Test
    fun `a 5xx is a problem only when the server named a code`() {
        assertTrue(readsAsProblem(503, "https://relaxkonos.app/problems/performance-not-ready", null))
        assertTrue(readsAsProblem(500, null, ProblemCodes.PERFORMANCE_NOT_READY))
        assertFalse(readsAsProblem(503, null, null))
        assertFalse(readsAsProblem(500, "https://tools.ietf.org/html/rfc9110#section-15.6.1", null))
    }

    @Test
    fun `a named 5xx code is explained to the user`() {
        assertEquals(
            R.string.error_performance_not_ready,
            problemMessage(ProblemCodes.PERFORMANCE_NOT_READY).resId,
        )
    }

    @Test
    fun `a 5xx is never a credential rejection`() {
        assertFalse(ApiResult.Problem(500, ProblemCodes.INVALID_CREDENTIAL, null).isCredentialRejection())
        assertFalse(ApiResult.Problem(503, ProblemCodes.PERFORMANCE_NOT_READY, null).isCredentialRejection())
    }

    @Test
    fun `sign-in throttling is explained rather than shown as a generic refusal`() {
        assertEquals(
            R.string.error_login_rate_limited,
            problemMessage(ProblemCodes.LOGIN_RATE_LIMITED).resId,
        )
    }

    @Test
    fun `sign-in throttling never counts as a rejected credential`() {
        // A 429 says the attempt was too soon, not that the password is wrong: dropping a stored
        // password over it would punish the user for retrying (`…LoginCredentials.Design.md` §7.3).
        assertFalse(ApiResult.Problem(429, ProblemCodes.LOGIN_RATE_LIMITED, null).isCredentialRejection())
    }

    @Test
    fun `the execution codes keep the wire spelling the server writes`() {
        // Mirror of `UserExecutionProblemTypes`: the client branches on these exact suffixes, so a
        // rename on either side has to fail a test rather than a user.
        assertEquals("identity-not-eligible", ProblemCodes.IDENTITY_NOT_ELIGIBLE)
        assertEquals("identity-not-executable", ProblemCodes.IDENTITY_NOT_EXECUTABLE)
        assertEquals(
            ProblemCodes.IDENTITY_NOT_ELIGIBLE,
            ProblemCodes.from("https://relaxkonos.app/problems/identity-not-eligible", null),
        )
        assertEquals(
            ProblemCodes.IDENTITY_NOT_EXECUTABLE,
            ProblemCodes.from("https://relaxkonos.app/problems/identity-not-executable", null),
        )
    }

    @Test
    fun `an identity the server refuses is explained rather than shown as a generic refusal`() {
        assertEquals(
            R.string.error_identity_not_eligible,
            problemMessage(ProblemCodes.IDENTITY_NOT_ELIGIBLE).resId,
        )
    }

    @Test
    fun `a missing execution channel is not told as an ineligible identity`() {
        // Two remedies, so two sentences: change the host account, or fix the deployment. One
        // sentence for both would send the user looking for the wrong fix.
        assertEquals(
            R.string.error_identity_not_executable,
            problemMessage(ProblemCodes.IDENTITY_NOT_EXECUTABLE).resId,
        )
    }

    @Test
    fun `the login reason picks the sentence its problem code would`() {
        assertEquals(
            R.string.error_identity_not_executable,
            executionEligibilityMessage(ExecutionEligibilityReasons.SERVER_ACCOUNT_REQUIRED).resId,
        )
        assertEquals(
            R.string.error_identity_not_eligible,
            executionEligibilityMessage(ExecutionEligibilityReasons.RESERVED_IDENTITY).resId,
        )
    }

    @Test
    fun `an unusable identity is never explained with silence`() {
        // `available = false` is authoritative even for a reason this build has never seen: a session
        // that says nothing is the half-broken file browser this notice exists to remove.
        listOf(
            ExecutionEligibilityReasons.SYSTEM_ACCOUNT,
            ExecutionEligibilityReasons.UNVERIFIED_HOME_DIRECTORY,
            ExecutionEligibilityReasons.WINDOWS_PROFILE_REQUIRED,
            ExecutionEligibilityReasons.UNSUPPORTED_PLATFORM,
            "a-reason-from-a-newer-server",
            null,
        ).forEach { reason ->
            assertEquals(
                "reason=$reason must still be explained",
                R.string.error_identity_not_eligible,
                executionEligibilityMessage(reason).resId,
            )
        }
    }
}
