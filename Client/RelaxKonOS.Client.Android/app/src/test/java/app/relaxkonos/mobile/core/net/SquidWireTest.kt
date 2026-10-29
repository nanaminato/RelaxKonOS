package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class SquidWireTest {
    @Test fun `access modes round trip without silently downgrading authentication`() {
        SquidAccessMode.entries.forEach { mode ->
            val original = SquidConfiguration(accessMode = mode)
            assertEquals(mode, SquidWire.configuration(original.json()).accessMode)
        }
        val json = SquidConfiguration().json()
        json.remove("accessMode")
        assertTrue(runCatching { SquidWire.configuration(json) }.isFailure)
        json.put("accessMode", "unknown")
        assertTrue(runCatching { SquidWire.configuration(json) }.isFailure)
    }
    @Test fun `package capability is independent and required`() {
        val payload = JSONObject("""{"state":{"configuration":null,"desiredRevision":0,"publishedRevision":null,"lastProblemCode":null},"status":{"supported":true,"installed":false,"running":false,"enabled":false,"managed":false,"configurationRevision":"","packageManagementSupported":true,"recoveryRequired":false},"matchesPublishedConfiguration":false}""")
        assertTrue(SquidWire.snapshot(payload.toString()).status.packageManagementSupported)
        val status = payload.getJSONObject("status")
        status.put("packageManagementSupported", false)
        val unsupported = SquidWire.snapshot(payload.toString()).status
        assertTrue(unsupported.supported)
        assertFalse(unsupported.packageManagementSupported)
        status.remove("packageManagementSupported")
        assertTrue(runCatching { SquidWire.snapshot(payload.toString()) }.isFailure)
    }
    @Test fun `account requests retain exact action credentials and idempotent replay body`() {
        val requests = listOf(
            SquidAccountMutation(SquidAccountAction.Create, "proxy.user", "secret123"),
            SquidAccountMutation(SquidAccountAction.SetPassword, "proxy.user", "newsecret123"),
            SquidAccountMutation(SquidAccountAction.SetEnabled, "proxy.user", enabled = false),
            SquidAccountMutation(SquidAccountAction.Delete, "proxy.user"))
        requests.forEach { account ->
            val request = SquidRequest(account = account)
            assertEquals(SquidWire.ROOT + "/accounts", request.route)
            assertEquals(request, SquidWire.request(JSONObject(request.json().toString())))
            assertEquals(account.action.name, request.json().getString("action"))
            account.password?.let { assertFalse(request.toString().contains(it)) }
        }
        assertEquals(listOf(SquidAccount("proxy.user", true)), SquidWire.accounts("""[{"username":"proxy.user","enabled":true}]"""))
        assertTrue(runCatching { SquidWire.accounts("""[{"username":"proxy.user"}]""") }.isFailure)
    }
    @Test fun `account validation rejects malformed credentials and mismatched action fields`() {
        listOf("", "-user", "has space", "a".repeat(65)).forEach { name ->
            assertTrue(runCatching { SquidAccountMutation(SquidAccountAction.Create, name, "secret123") }.isFailure)
        }
        listOf("short", "x".repeat(129), "secret\n123", "secret\u007f123").forEach { password ->
            assertTrue(runCatching { SquidAccountMutation(SquidAccountAction.Create, "user", password) }.isFailure)
        }
        assertTrue(runCatching { SquidAccountMutation(SquidAccountAction.Delete, "user", password = "secret123") }.isFailure)
        assertTrue(runCatching { SquidAccountMutation(SquidAccountAction.SetEnabled, "user") }.isFailure)
        assertTrue(runCatching { SquidRequest(action = SquidAction.Install, account = SquidAccountMutation(SquidAccountAction.Delete, "user")) }.isFailure)
    }
    @Test fun `package actions use lifecycle route and survive request journal round trip`() {
        listOf(SquidAction.Install, SquidAction.Uninstall).forEach { action ->
            val request = SquidRequest(action = action)
            assertEquals(SquidWire.ROOT + "/lifecycle", request.route)
            assertEquals(action.name.lowercase(), request.json().getString("action"))
            assertEquals(request, SquidWire.request(request.json()))
        }
    }
    @Test fun `pending credentials are encrypted authenticated and bound to journal identity`() {
        val key = javax.crypto.KeyGenerator.getInstance("AES").apply { init(256) }.generateKey()
        val request = SquidRequest(account = SquidAccountMutation(SquidAccountAction.Create, "user", "secret123"))
        val plaintext = request.json().toString()
        val cipher = app.relaxkonos.mobile.data.SquidPendingCipher
        val encrypted = cipher.encrypt(key, plaintext, "server-account-workspace")
        assertFalse(String(encrypted, Charsets.ISO_8859_1).contains("secret123"))
        assertEquals(request, SquidWire.request(JSONObject(cipher.decrypt(key, encrypted, "server-account-workspace"))))
        assertTrue(runCatching { cipher.decrypt(key, encrypted, "other-account") }.isFailure)
        val tampered = encrypted.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }
        assertTrue(runCatching { cipher.decrypt(key, tampered, "server-account-workspace") }.isFailure)
        assertTrue(runCatching { cipher.decrypt(key, encrypted.copyOf(12), "server-account-workspace") }.isFailure)
        assertFalse(encrypted.contentEquals(cipher.encrypt(key, plaintext, "server-account-workspace")))
    }
}
