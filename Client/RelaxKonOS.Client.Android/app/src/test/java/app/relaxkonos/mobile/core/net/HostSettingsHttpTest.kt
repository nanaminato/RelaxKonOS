package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class HostSettingsHttpTest {
    private val id="ca57b835-8242-47bb-8a2b-c3a5f4658a6b"
    private val revision="A".repeat(64)
    private val target="""{"resourceId":"host/environment/machine","scope":"hostMachine","platformIdentity":null}"""
    private val plan="""{"planId":"$id","target":$target,"expectedRevision":"$revision","expiresAt":"2099-10-01T00:00:00Z","differences":[{"settingId":"X","before":null,"after":"[configured]"}],"requiredCapability":"hostEnvironmentChange","authorizationTarget":"host/environment/machine","effectiveState":"newLogin","impactCode":"settings.environment.new_login_required"}"""
    @Test fun `environment preview sends empty value distinct from delete and apply only sends original plan`()=runTest {
        val bodies=mutableListOf<JSONObject>();val routes=mutableListOf<String>();val keys=mutableListOf<String?>()
        val server=HttpServer.create(InetSocketAddress("127.0.0.1",0),0)
        server.createContext("/"){ex->
            routes.add(ex.requestMethod+" "+ex.requestURI);bodies.add(JSONObject(ex.requestBody.bufferedReader().use{it.readText()}));keys.add(ex.requestHeaders.getFirst("Idempotency-Key"))
            val response=if(ex.requestURI.path.endsWith("preview")) plan else """{"operationId":"$id","settingId":"host.environment","target":$target,"state":"applied","updatedAt":"2026-10-01T00:00:00Z","observedRevision":"$revision","problemCode":null,"effectiveState":"newLogin"}"""
            val bytes=response.toByteArray();ex.sendResponseHeaders(200,bytes.size.toLong());ex.responseBody.use{it.write(bytes)};ex.close()
        }
        server.start()
        try {
            val url="http://127.0.0.1:${server.address.port}";val api=RelaxKonApi("test","test")
            assertTrue(api.previewHostSettings(url,"token",HostSettingKind.Environment,revision,"original-key",null,HostEnvironmentScope.HostMachine,HostEnvironmentMutation("X",false,""),false) is ApiResult.Success)
            assertTrue(api.applyHostSettings(url,"token",HostSettingKind.Environment,id) is ApiResult.Success)
            assertEquals("POST /api/v1.0/host-settings/environment/preview",routes[0]);assertEquals("POST /api/v1.0/host-settings/environment/apply",routes[1])
            assertEquals("hostMachine",bodies[0].getString("scope"));assertEquals("original-key",bodies[0].getString("idempotencyKey"))
            val m=bodies[0].getJSONObject("change").getJSONArray("changes").getJSONObject(0)
            assertEquals("set",m.getString("operation"));assertEquals("",m.getString("value"));assertEquals("string",m.getString("valueKind"))
            assertEquals(1,bodies[1].length());assertEquals(id,bodies[1].getString("planId"));assertTrue(keys.all{it==null})
        }finally{server.stop(0)}
    }
    @Test fun `production problemCode extension survives settings 428 refusal`()=runTest {
        val server=HttpServer.create(InetSocketAddress("127.0.0.1",0),0)
        server.createContext("/"){ex->val bytes="""{"type":"about:blank","title":"settings.environment.authorization_required","status":428,"problemCode":"settings.environment.authorization_required"}""".toByteArray();ex.sendResponseHeaders(428,bytes.size.toLong());ex.responseBody.use{it.write(bytes)};ex.close()}
        server.start()
        try{
            val result=RelaxKonApi("test","test").hostEnvironment("http://127.0.0.1:${server.address.port}","token",HostEnvironmentScope.HostMachine,false)
            assertEquals("settings.environment.authorization_required",(result as ApiResult.Problem).code)
        }finally{server.stop(0)}
    }
}
