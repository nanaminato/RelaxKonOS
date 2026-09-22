package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.AuthTokens
import app.relaxkonos.mobile.core.net.ElevationGrant
import app.relaxkonos.mobile.core.net.ProblemCodes
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.security.CredentialVault
import app.relaxkonos.mobile.security.FakeVaultCrypto
import app.relaxkonos.mobile.security.InMemoryVaultStorage
import app.relaxkonos.mobile.security.VaultKind
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The elevation rules of `RelaxKonOS.Mobile.V1.Design.md` §4.3 and §5.8.2: one grant, one retry, no
 * loop, grants bound to the access token, and a stored administrator password discarded on any server
 * rejection but never on a transport failure or a user cancellation.
 */
class ElevationRepositoryTest {
    private val server = "https://relaxkonos.local:5090"
    private val gateway = FakeGateway()
    private val vault = CredentialVault(InMemoryVaultStorage(), FakeVaultCrypto())
    private val session = AuthSession(gateway)
    private val repository = ElevationRepository(gateway, session, vault)

    private suspend fun signIn() {
        gateway.onLogin = { _, _, _ -> ApiResult.Success(loginSession()) }
        session.login(server, "nana", "pw".toCharArray())
    }

    private fun seedElevationCredential(account: String, password: String) {
        vault.seal(
            kind = VaultKind.Elevation,
            serverUrl = server,
            account = account,
            password = password.toCharArray(),
            cipher = vault.beginSeal(VaultKind.Elevation),
            fingerprintProtected = true,
            nowEpochMillis = 1_000L,
        )
    }

    private fun providerReturning(account: String, password: String) =
        ElevationAnswerProvider { _, _ -> ElevationAnswer(account, password.toCharArray()) }

    @Test
    fun `elevation required retries the same call exactly once after a grant`() = runTest {
        signIn()
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Success(ElevationGrant(elevated = true, expiresAtMillis = null)) }
        var attempts = 0

        val result = repository.withElevation<String>(
            capability = "read",
            target = "/etc/shadow",
            provider = providerReturning("admin", "s3cret"),
        ) { _, _ ->
            attempts++
            if (attempts == 1) {
                ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null)
            } else {
                ApiResult.Success("contents")
            }
        }

        assertTrue(result is ApiResult.Success)
        assertEquals(2, attempts)
        assertEquals(1, gateway.elevationCount)
        assertEquals(listOf("/etc/shadow"), gateway.elevationTargets)
    }

    @Test
    fun `a second elevation-required is returned without another attempt`() = runTest {
        signIn()
        // The server accepted the call but granted nothing, so the retry would fail identically.
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Success(ElevationGrant(elevated = false, expiresAtMillis = null)) }
        var attempts = 0

        val result = repository.withElevation<String>(
            capability = "delete",
            target = "/etc/nginx",
            provider = providerReturning("admin", "s3cret"),
        ) { _, _ ->
            attempts++
            ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null)
        }

        assertTrue(result is ApiResult.Problem)
        assertEquals(ProblemCodes.ELEVATION_REQUIRED, (result as ApiResult.Problem).code)
        assertEquals("the operation must not be retried after a refused elevation", 1, attempts)
        assertEquals(1, gateway.elevationCount)
    }

    @Test
    fun `a cancelled prompt contacts nobody and keeps the stored credential`() = runTest {
        signIn()
        seedElevationCredential("admin", "s3cret")
        val result = repository.withElevation<String>(
            capability = "delete",
            target = "/etc/nginx",
            provider = { _, _ -> null },
        ) { _, _ -> ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null) }

        assertTrue(result is ApiResult.Problem)
        assertEquals(0, gateway.elevationCount)
        assertNotNull(vault.record(VaultKind.Elevation, server, "admin"))
    }

    @Test
    fun `any server rejection discards the stored administrator password`() = runTest {
        signIn()
        seedElevationCredential("admin", "s3cret")
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Problem(403, ProblemCodes.ELEVATION_PASSWORD_INVALID, null) }

        val outcome = repository.elevateHost("nativeServiceAction", "1234", providerReturning("admin", "wrong"))

        assertTrue(outcome is ElevationOutcome.Rejected)
        assertTrue((outcome as ElevationOutcome.Rejected).credentialDiscarded)
        assertNull(vault.record(VaultKind.Elevation, server, "admin"))
    }

    @Test
    fun `a rejection that is not about the credential still discards it`() = runTest {
        signIn()
        seedElevationCredential("admin", "s3cret")
        // The rule is deliberately blunt: no rejection path is allowed to skip the cleanup.
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Problem(500, "internal-error", null) }

        val outcome = repository.elevateHost("nativeServiceAction", "1234", providerReturning("admin", "s3cret"))

        assertTrue(outcome is ElevationOutcome.Rejected)
        assertNull(vault.record(VaultKind.Elevation, server, "admin"))
    }

    @Test
    fun `a transport failure keeps the stored administrator password`() = runTest {
        signIn()
        seedElevationCredential("admin", "s3cret")
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Transport("timeout") }

        val outcome = repository.elevateHost("nativeServiceAction", "1234", providerReturning("admin", "s3cret"))

        assertTrue(outcome is ElevationOutcome.Transport)
        assertNotNull(vault.record(VaultKind.Elevation, server, "admin"))
    }

    @Test
    fun `a rejection discards only the account that was tried`() = runTest {
        signIn()
        seedElevationCredential("admin", "s3cret")
        seedElevationCredential("other-admin", "s3cret")
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Problem(403, ProblemCodes.ELEVATION_ACCOUNT_NOT_ADMINISTRATOR, null) }

        repository.elevateHost("nativeServiceAction", "1234", providerReturning("admin", "s3cret"))

        assertNull(vault.record(VaultKind.Elevation, server, "admin"))
        assertNotNull(vault.record(VaultKind.Elevation, server, "other-admin"))
    }

    @Test
    fun `the password is zeroed once the request has been sent`() = runTest {
        signIn()
        val password = "s3cret".toCharArray()
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Success(ElevationGrant(elevated = true, expiresAtMillis = null)) }

        repository.elevateHost("nativeServiceAction", "1234", ElevationAnswerProvider { _, _ -> ElevationAnswer("admin", password) })

        assertTrue("the submitted password must not survive the call", password.all { it == '\u0000' })
    }

    @Test
    fun `the account the user typed is the one that is submitted`() = runTest {
        signIn()
        seedElevationCredential("stale-admin", "s3cret")
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Success(ElevationGrant(elevated = true, expiresAtMillis = null)) }

        repository.elevateHost("nativeServiceAction", "1234", providerReturning("typed-admin", "s3cret"))

        assertEquals(listOf<String?>("typed-admin"), gateway.elevationAccounts)
    }

    @Test
    fun `a grant is remembered for the exact capability and target`() = runTest {
        signIn()
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Success(ElevationGrant(elevated = true, expiresAtMillis = null)) }

        repository.elevateHost("read", "/etc/shadow", providerReturning("admin", "s3cret"))
        val now = System.currentTimeMillis()

        assertTrue(repository.hasGrant("read", "/etc/shadow", now))
        assertFalse(repository.hasGrant("write", "/etc/shadow", now))
        assertFalse(repository.hasGrant("read", "/etc/hosts", now))
    }

    @Test
    fun `refreshing the access token invalidates every remembered grant`() = runTest {
        signIn()
        gateway.onElevation = { _, _, _, _, _, _ -> ApiResult.Success(ElevationGrant(elevated = true, expiresAtMillis = null)) }
        repository.elevateHost("read", "/etc/shadow", providerReturning("admin", "s3cret"))
        assertTrue(repository.hasGrant("read", "/etc/shadow", System.currentTimeMillis()))

        gateway.onRefresh = { _, _ -> ApiResult.Success(AuthTokens("access-2", "refresh-2", null, null)) }
        session.renew()

        assertFalse(repository.hasGrant("read", "/etc/shadow", System.currentTimeMillis()))
    }

    @Test
    fun `file elevation uses the file entry point and the granted path`() = runTest {
        signIn()
        var requestedPath: String? = null
        var requestedCapability: String? = null
        gateway.onFileElevation = { _, _, path, capability, _, _, includeDescendants, _ ->
            requestedPath = path
            requestedCapability = capability
            assertEquals(true, includeDescendants)
            ApiResult.Success(app.relaxkonos.mobile.core.net.FileElevationGrant(requiresElevation = true, elevated = true, expiresAtMillis = null))
        }

        val outcome = repository.elevatePath(
            path = "/etc/nginx",
            capability = "delete",
            relatedPaths = emptyList(),
            includeDescendants = true,
            provider = providerReturning("admin", "s3cret"),
        )

        assertTrue(outcome is ElevationOutcome.Granted)
        assertEquals("/etc/nginx", requestedPath)
        assertEquals("delete", requestedCapability)
    }

    @Test
    fun `path elevation retries the file call once`() = runTest {
        signIn()
        gateway.onFileElevation = { _, _, _, _, _, _, _, _ ->
            ApiResult.Success(app.relaxkonos.mobile.core.net.FileElevationGrant(requiresElevation = true, elevated = true, expiresAtMillis = null))
        }
        var attempts = 0

        val result = repository.withPathElevation<String>(
            path = "/etc/nginx",
            capability = "delete",
            provider = providerReturning("admin", "s3cret"),
        ) { _, _ ->
            attempts++
            if (attempts == 1) {
                ApiResult.Problem(403, ProblemCodes.ELEVATION_REQUIRED, null)
            } else {
                ApiResult.Success("gone")
            }
        }

        assertTrue(result is ApiResult.Success)
        assertEquals(2, attempts)
    }
}
