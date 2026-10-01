package app.relaxkonos.mobile.core.net

import com.google.gson.JsonElement
import com.microsoft.signalr.GsonHubProtocol
import com.microsoft.signalr.InvocationBinder
import com.microsoft.signalr.InvocationMessage
import java.lang.reflect.Type
import java.nio.ByteBuffer
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class PerformanceSignalRTest {
    private fun event(body: String): JsonElement {
        val binder = object : InvocationBinder {
            override fun getReturnType(id: String): Type = Void.TYPE
            override fun getParameterTypes(name: String): List<Type> {
                assertEquals("OnPerformanceSnapshot", name); return listOf(JsonElement::class.java)
            }
        }
        val message = """{"type":1,"target":"OnPerformanceSnapshot","arguments":[$body]}""" + 30.toChar()
        val frame = GsonHubProtocol().parseMessages(ByteBuffer.wrap(message.toByteArray()), binder).single() as InvocationMessage
        assertEquals("OnPerformanceSnapshot", frame.target)
        return frame.arguments.single() as JsonElement
    }
    @Test fun `actual SignalR Gson binding retains nulls and full integer precision`() {
        val snapshot = PerformanceWire.snapshot(event(PerformanceWireTest.SNAPSHOT).toString())
        assertNull(snapshot.cpu.currentFrequencyMHz)
        assertNull(snapshot.networks.single().sendErrors)
        assertEquals(9007199254740993L, snapshot.networks.single().bytesReceived)
    }
    @Test fun `actual SignalR binding does not manufacture missing metric fields`() {
        val malformed = JSONObject(PerformanceWireTest.SNAPSHOT).apply { getJSONObject("cpu").remove("userPercent") }.toString()
        assertTrue(runCatching { PerformanceWire.snapshot(event(malformed).toString()) }.isFailure)
    }
}
