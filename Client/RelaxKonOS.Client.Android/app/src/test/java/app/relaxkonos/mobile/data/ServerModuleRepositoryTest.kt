package app.relaxkonos.mobile.data

import app.relaxkonos.mobile.FakeGateway
import app.relaxkonos.mobile.loginSession
import app.relaxkonos.mobile.core.auth.AuthSession
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.servercenter.ServerConnectionIdentityRules
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ServerModuleRepositoryTest {
    private val plan = ServerModulesWire.plan("""{"planId":"10000000-0000-0000-0000-000000000001","serviceId":"host-a",
        "expiresAt":"2099-01-01T00:00:00Z","expectedRevision":"revision","target":{"serviceId":"host-a","action":"Disable","moduleIds":["relaxkonos.git"],"enabledModuleIds":[],"expectedRevision":"revision","idempotencyKey":"key"},"restartRequired":true,"added":[],"removed":["relaxkonos.git"],"blockedBy":[]}""")
    private val fact = ServerModuleFact("relaxkonos.git", "host-a", true, true, false, "revision", "generation",
        "1.0", "dormant", "IdleDelayed", 0, emptyList(), emptyList(), null, null, setOf("Disable", "Enable"))

    @Test fun `lost apply reply survives repository recreation and recovery only reads original plan`() = runTest {
        val directory = java.nio.file.Files.createTempDirectory("module-recovery").toFile()
        try {
            val login = FakeGateway().apply { onLogin = { _, _, _ -> ApiResult.Success(loginSession()) } }
            lateinit var owner: SessionState.Active
            val references = ServerModuleReferences(directory)
            var applies = 0
            var reads = 0
            var frozenPlan = plan
            val gateway = object : RelaxKonGateway by login {
                override suspend fun serverModules(serverUrl: String, accessToken: String) = ApiResult.Success(listOf(fact))
                override suspend fun serverModulePlan(serverUrl: String, accessToken: String, body: JsonBody): ApiResult<ServerModulePlan> {
                    val json = JSONObject(body.toByteArray().toString(Charsets.UTF_8))
                    assertEquals("host-a", json.getString("serviceId"))
                    assertEquals("Disable", json.getString("action"))
                    assertEquals(0, json.getJSONArray("enabledModuleIds").length())
                    val frozen = JSONObject(plan.raw).put("target", json)
                    frozenPlan = ServerModulesWire.plan(frozen.toString())
                    return ApiResult.Success(frozenPlan)
                }
                override suspend fun applyServerModulePlan(serverUrl: String, accessToken: String, planId: String): ApiResult<ServerModuleOperation> {
                    applies++
                    assertEquals(plan.id, references.read(owner).single().id)
                    return ApiResult.Transport(null)
                }
                override suspend fun serverModulePlanStatus(serverUrl: String, accessToken: String, planId: String): ApiResult<ServerModulePlanStatus> {
                    reads++; assertEquals(plan.id, planId)
                    return ApiResult.Success(ServerModulePlanStatus(frozenPlan, ServerModuleOperation("operation", plan.id, "host-a", "AwaitingRestart", emptyList(), null)))
                }
            }
            val session = AuthSession(gateway)
            session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
            owner = session.state.value as SessionState.Active
            val repository = ServerModuleRepository(session, gateway, references)
            assertTrue(repository.read(owner) is ApiResult.Success)
            assertTrue(repository.preview(owner, listOf(fact), setOf(fact.id), false) is ApiResult.Success)
            assertEquals(0, applies)
            assertTrue(repository.apply(owner, frozenPlan) is ApiResult.Transport)
            val reopened = ServerModuleRepository(session, gateway, ServerModuleReferences(directory))
            reopened.read(owner)
            assertEquals("AwaitingRestart", reopened.recover(owner, listOf(fact)).single().operation?.state)
            assertEquals(1, applies); assertEquals(1, reads)
            assertTrue(references.read(owner.copy(serviceId = "other")).isEmpty())
            assertTrue(references.read(owner.copy(userName = "other")).isEmpty())
            assertTrue(references.read(owner.copy(workspaceId = "other")).isEmpty())
            session.login(ServerConnectionIdentityRules.direct("https://other.test"), "alice", "pw".toCharArray()) {}
            assertTrue(runCatching { repository.apply(owner, plan) }.isFailure)
            assertEquals(1, applies)
        } finally { directory.deleteRecursively() }
    }

    @Test fun `reference conflicts and oversized storage fail closed`() = runTest {
        val directory = java.nio.file.Files.createTempDirectory("module-references").toFile()
        try {
            val gateway = FakeGateway().apply { onLogin = { _, _, _ -> ApiResult.Success(loginSession()) } }
            val session = AuthSession(gateway)
            session.login(ServerConnectionIdentityRules.direct("https://example.test"), "alice", "pw".toCharArray()) {}
            val owner = session.state.value as SessionState.Active
            val references = ServerModuleReferences(directory)
            references.save(owner, plan)
            references.save(owner, plan)
            assertEquals(1, references.read(owner).size)
            assertTrue(runCatching { references.save(owner, plan.copy(raw = "different")) }.isFailure)
            directory.listFiles()!!.single().writeBytes(ByteArray(524289))
            assertTrue(runCatching { references.read(owner) }.isFailure)
        } finally { directory.deleteRecursively() }
    }
}
