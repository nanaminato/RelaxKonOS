package app.relaxkonos.mobile.core.net

import com.sun.net.httpserver.HttpServer
import java.net.InetSocketAddress
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class PerformanceWireTest {
    @Test fun `complete snapshot retains unknown values and exact large counters`() {
        val value = PerformanceWire.snapshot(SNAPSHOT)
        assertEquals(7L, value.sequence)
        assertEquals(listOf(0.0, 37.5), value.cpu.perLogicalCpuPercent)
        assertNull(value.cpu.currentFrequencyMHz)
        assertNull(value.cpu.handleCount)
        assertEquals(9007199254740993L, value.networks.single().bytesReceived)
        assertEquals(0L, value.networks.single().receiveErrors)
        assertNull(value.networks.single().sendErrors)
        assertNull(value.disks.single().latencyMs)
        assertEquals(2.5, value.disks.single().queueLength!!, 0.0)
        assertEquals(512L, value.memory.cachedBytes)
        assertEquals(10L, value.filesystems.single().availableBytes)
        assertTrue(value.health.isStale)
        assertEquals("sampling-unavailable", value.health.error)
    }
    @Test fun `required fields are never replaced with synthetic zero or empty arrays`() {
        for (key in listOf("sequence", "health", "networks", "disks", "timestamp")) {
            rejected { PerformanceWire.snapshot(JSONObject(SNAPSHOT).apply { remove(key) }.toString()) }
        }
        rejected { PerformanceWire.snapshot(JSONObject(SNAPSHOT).apply { getJSONObject("cpu").remove("userPercent") }.toString()) }
        rejected { PerformanceWire.snapshot("""{"cpu":{"totalPercent":0},"memory":{"totalBytes":1,"usedBytes":0},"filesystems":[],"uptimeSeconds":1}""") }
    }
    @Test fun `malformed timestamps types and numeric ranges are rejected`() {
        for (value in listOf(-1, 100.01, "1")) rejected {
            PerformanceWire.snapshot(JSONObject(SNAPSHOT).apply { getJSONObject("cpu").put("totalPercent", value) }.toString())
        }
        rejected { PerformanceWire.snapshot(JSONObject(SNAPSHOT).apply { getJSONArray("networks").getJSONObject(0).put("bytesSent", 1.5) }.toString()) }
        rejected { PerformanceWire.snapshot(JSONObject(SNAPSHOT).apply { put("timestamp", "yesterday") }.toString()) }
        rejected { PerformanceWire.snapshot(JSONObject(SNAPSHOT).apply { getJSONObject("health").put("isStale", "false") }.toString()) }
    }
    @Test fun `history is bounded ordered and supports a genuine empty response`() {
        assertTrue(PerformanceWire.history("[]").isEmpty())
        val first = JSONObject(SNAPSHOT).put("sequence", 1)
        val second = JSONObject(SNAPSHOT).put("sequence", 2)
        assertEquals(listOf(1L, 2L), PerformanceWire.history(JSONArray().put(first).put(second).toString()).map { it.sequence })
        rejected { PerformanceWire.history(JSONArray().put(second).put(first).toString()) }
        rejected { PerformanceWire.history(JSONArray().put(first).put(first).toString()) }
        rejected { PerformanceWire.history(JSONArray(List(61) { JSONObject(SNAPSHOT).put("sequence", it) }).toString()) }
    }
    @Test fun `static identity capabilities and nullable hardware detail remain explicit`() {
        val info = PerformanceWire.info(INFO)
        assertFalse(info.capabilities.diskLatency)
        assertFalse(info.capabilities.gpu)
        assertNull(info.cpu.model)
        assertNull(info.cpu.virtualizationEnabled)
        assertEquals(4, info.cpu.logicalProcessorCount)
        assertEquals("/data", info.filesystems.single().mountPoint)
        assertEquals(listOf("fs"), info.disks.single().filesystemIds)
        assertEquals(listOf("192.0.2.1", "2001:db8::1"), info.networks.single().addresses)
        assertNull(info.networks.single().linkSpeedBitsPerSecond)
        rejected { PerformanceWire.info(JSONObject(INFO).apply { getJSONObject("capabilities").remove("diskIo") }.toString()) }
        rejected { PerformanceWire.info(JSONObject(INFO).apply { getJSONObject("cpu").put("logicalProcessorCount", 4.5) }.toString()) }
    }
    @Test fun `current HTTP info snapshot history and addresses use authentication and strict payloads`() = runBlocking {
        val requests = mutableListOf<String>()
        var response = ""
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            requests += exchange.requestMethod + " " + exchange.requestURI.toString()
            assertEquals("Bearer token", exchange.requestHeaders.getFirst("Authorization"))
            val bytes = response.toByteArray(); exchange.sendResponseHeaders(200, bytes.size.toLong())
            exchange.responseBody.use { it.write(bytes) }
        }
        server.start()
        try {
            val api = RelaxKonApi("test", "test"); val url = "http://127.0.0.1:${server.address.port}"
            response = INFO; assertTrue(api.performanceInfo(url, "token") is ApiResult.Success)
            response = SNAPSHOT; assertTrue(api.performanceSnapshot(url, "token") is ApiResult.Success)
            response = "[$SNAPSHOT]"; assertTrue(api.performanceHistory(url, "token") is ApiResult.Success)
            response = """[{"interfaceName":"eth0","address":"192.0.2.1","family":"IPv4"}]"""
            assertEquals("eth0", (api.networkAddresses(url, "token") as ApiResult.Success).value.single().interfaceName)
            response = "{}"; assertTrue(api.performanceSnapshot(url, "token") is ApiResult.Transport)
            assertEquals(listOf("GET /api/v1.0/system/performance/info", "GET /api/v1.0/system/performance/snapshot",
                "GET /api/v1.0/system/performance/history?seconds=60", "GET /api/v1.0/system/network-addresses",
                "GET /api/v1.0/system/performance/snapshot"), requests)
        } finally { server.stop(0) }
    }
    private fun rejected(action: () -> Unit) { assertTrue(runCatching(action).isFailure) }
    companion object {
        const val SNAPSHOT = """{
            "sequence":7,"timestamp":"2026-10-01T00:00:00.1234567Z","uptimeSeconds":300,
            "cpu":{"totalPercent":20,"userPercent":10,"systemPercent":10,"idlePercent":80,"iowaitPercent":null,
                "perLogicalCpuPercent":[0,37.5],"currentFrequencyMHz":null,"processCount":2,"threadCount":4,"handleCount":null},
            "memory":{"totalBytes":4096,"usedBytes":2048,"availableBytes":2048,"cachedBytes":512,"bufferedBytes":null,"swapUsedBytes":0,"swapTotalBytes":1024},
            "filesystems":[{"id":"fs","totalBytes":20,"usedBytes":10,"availableBytes":10,"percent":50}],
            "disks":[{"id":"disk","readBytesPerSecond":0,"writeBytesPerSecond":8,"readIops":0,"writeIops":2,"activityPercent":20,"queueLength":2.5,"latencyMs":null}],
            "networks":[{"id":"nic","bytesReceived":9007199254740993,"bytesSent":40,"receiveBytesPerSecond":0,"sendBytesPerSecond":8,
                "receivePackets":1,"sendPackets":2,"receiveErrors":0,"sendErrors":null,"receiveDropped":null,"sendDropped":0}],
            "health":{"isStale":true,"lastSuccessfulSampleAt":"2026-10-01T00:00:00.1234567Z","error":"sampling-unavailable"}
        }"""
        const val INFO = """{
            "cpu":{"model":null,"physicalCoreCount":2,"logicalProcessorCount":4,"baseFrequencyMHz":null,"virtualizationEnabled":null,
                "socketCount":1,"l1CacheBytes":null,"l2CacheBytes":null,"l3CacheBytes":null},
            "memory":{"totalBytes":4096,"swapTotalBytes":null},
            "filesystems":[{"id":"fs","name":"data","mountPoint":"/data"}],
            "disks":[{"id":"disk","name":"disk0","model":null,"filesystemIds":["fs"]}],
            "networks":[{"id":"nic","name":"eth0","linkSpeedBitsPerSecond":null,"addresses":["192.0.2.1","2001:db8::1"]}],
            "capabilities":{"perLogicalCpu":true,"cpuFrequency":false,"cpuIowait":false,"diskIo":true,"diskLatency":false,"diskQueueLength":true,"networkErrors":false,"gpu":false}
        }"""
    }
}
