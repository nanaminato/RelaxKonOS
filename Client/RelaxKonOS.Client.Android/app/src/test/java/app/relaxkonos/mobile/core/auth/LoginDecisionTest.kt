package app.relaxkonos.mobile.core.auth

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The sign-in decision table of `RelaxKonOS.Mobile.LoginCredentials.Design.md` §5.1, row by row.
 *
 * This is the table the single sign-in button follows. Keeping it as a pure function is what makes it
 * possible to state every row here instead of reasoning about a Compose click handler.
 */
class LoginDecisionTest {
    private val login = SelectedLogin.direct("https://relaxkonos.local:5090", "nana")

    private fun decide(
        password: String = "",
        credential: SavedCredentialState = SavedCredentialState.Absent,
        isLoggingIn: Boolean = false,
        selected: SelectedLogin = login,
    ): LoginDecision? = decideLogin(selected, password, credential, isLoggingIn)

    /** Row 1: an incomplete identity is reported before anything else, including an in-flight sign-in. */
    @Test
    fun `incomplete fields are reported first`() {
        assertEquals(
            LoginDecision.MissingFields(server = true, identifier = false),
            decide(selected = SelectedLogin.direct("   ", "nana"), isLoggingIn = true),
        )
        assertEquals(
            LoginDecision.MissingFields(server = false, identifier = true),
            decide(
                selected = SelectedLogin.direct("https://relaxkonos.local:5090", "  "),
                credential = SavedCredentialState.Available,
            ),
        )
    }

    /** Row 2: a second click while a sign-in is in flight does nothing. */
    @Test
    fun `a click while signing in is ignored`() {
        assertNull(decide(credential = SavedCredentialState.Available, isLoggingIn = true))
        assertNull(decide(password = "typed", isLoggingIn = true))
    }

    /** Row 3: a typed password wins over a stored one, which is ignored — not replaced (§5.1, §7.3). */
    @Test
    fun `a typed password beats a saved credential`() {
        val decision = decide(password = "typed", credential = SavedCredentialState.Available)

        assertTrue(decision is LoginDecision.ManualPassword)
        assertEquals("typed", String((decision as LoginDecision.ManualPassword).password))
    }

    /** Emptiness is the test, not blankness: a password may contain spaces and is never trimmed. */
    @Test
    fun `a password of spaces counts as a typed password`() {
        assertTrue(decide(password = " ") is LoginDecision.ManualPassword)
    }

    /** Row 4: with the field empty, a usable stored password is unsealed rather than asked for. */
    @Test
    fun `an empty field with a usable credential unseals it`() {
        assertEquals(LoginDecision.UnlockSavedCredential, decide(credential = SavedCredentialState.Available))
    }

    /** Rows 5–7: no usable stored password means the user has to type one, and the gap decides the text. */
    @Test
    fun `an empty field without a usable credential asks for a password`() {
        assertEquals(
            LoginDecision.RequirePassword(CredentialGap.Absent),
            decide(credential = SavedCredentialState.Absent),
        )
        assertEquals(
            LoginDecision.RequirePassword(CredentialGap.Unavailable),
            decide(credential = SavedCredentialState.Unavailable),
        )
        assertEquals(
            LoginDecision.RequirePassword(CredentialGap.Invalidated),
            decide(credential = SavedCredentialState.Invalidated),
        )
    }

    /** An empty password is never submitted, whatever is stored: the server has no empty-password case (D8). */
    @Test
    fun `no decision ever carries an empty password`() {
        val decision = decide(password = "", credential = SavedCredentialState.Absent)

        assertTrue(decision is LoginDecision.RequirePassword)
    }
}
