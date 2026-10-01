package app.relaxkonos.mobile.core.net

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class GuardianContractsTest {
    private val definition = GuardianDefinition("worker", "Worker", "/bin/job", listOf("", " two words ", "a\nb"), "/jobs", true,
        49, 17, GuardianHealthCheck("http", "http://localhost:8080/health", 37, 11, 9), "worker", "uid:1042")

    private fun read(value: JSONObject) = GuardianWire.definition(JSONObject().put("success", true).put("problemCode", "").put("definition", value).toString()).definition

    @Test fun `current definition round trip retains every field and exact argument elements`() {
        val body = JSONObject(GuardianWire.definitionJson(definition))
        assertEquals(11, body.length())
        assertEquals(definition, read(body))
        assertEquals("uid:1042", body.getString("runAsIdentity"))
    }

    @Test fun `required numeric boolean and argument fields cannot silently become defaults`() {
        listOf("stopTimeoutSeconds", "maxRestartAttempts", "enabledOnBoot", "arguments").forEach { name ->
            val body = JSONObject(GuardianWire.definitionJson(definition)); body.remove(name)
            assertTrue(name, runCatching { read(body) }.isFailure)
        }
        listOf("intervalSeconds", "timeoutSeconds", "failureThreshold").forEach { name ->
            val body = JSONObject(GuardianWire.definitionJson(definition)); body.getJSONObject("healthCheck").remove(name)
            assertTrue(name, runCatching { read(body) }.isFailure)
        }
        listOf<Any>("49", 3.5, 2147483648L).forEach { value ->
            assertTrue(runCatching { read(JSONObject(GuardianWire.definitionJson(definition)).put("stopTimeoutSeconds", value)) }.isFailure)
        }
        assertTrue(runCatching { read(JSONObject(GuardianWire.definitionJson(definition)).put("enabledOnBoot", "true")) }.isFailure)
    }

    @Test fun `nullable health and stable identity stay absent without changing other fields`() {
        val value = definition.copy(healthCheck = null, runAsIdentity = null)
        assertEquals(value, read(JSONObject(GuardianWire.definitionJson(value))))
    }

    @Test fun `failed agent receipts remain failures and malformed successes are rejected`() {
        assertEquals(GuardianDefinitionResult(false, "guardian.workload_not_found", null),
            GuardianWire.definition("{\"success\":false,\"problemCode\":\"guardian.workload_not_found\"}"))
        assertTrue(runCatching { GuardianWire.definition("{\"success\":true,\"problemCode\":\"\"}") }.isFailure)
        assertTrue(runCatching { GuardianWire.operation("{\"success\":\"false\",\"problemCode\":\"\"}") }.isFailure)
    }

    @Test fun `IDs use path encoding rather than form plus encoding`() {
        assertEquals("/api/v1.0/guardian/workloads/two%20words", GuardianRoutes.workload("two words"))
        assertTrue(runCatching { GuardianRoutes.workload("..") }.isFailure)
    }
}
