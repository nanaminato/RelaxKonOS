package app.relaxkonos.mobile.core.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Problem handling, asserted because the distinction between a `Problem` and a `Transport` is what
 * decides whether a stored credential is deleted (`RelaxKonOS.Mobile.V1.Design.md` §5.8.2).
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
}
