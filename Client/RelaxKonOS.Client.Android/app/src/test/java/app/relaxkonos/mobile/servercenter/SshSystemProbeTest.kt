package app.relaxkonos.mobile.servercenter

import org.junit.Assert.*
import org.junit.Test

class SshSystemProbeTest {
    @Test fun windowsProbeFitsCmdLimitAndPreservesHereString() {
        assertTrue(SshSystemProbe.windowsCommand.length < 8191)
        val encoded = SshSystemProbe.windowsCommand.substringAfter("-EncodedCommand ")
        val script = String(java.util.Base64.getDecoder().decode(encoded), Charsets.UTF_16LE)
        assertTrue(script.contains("\n'@\n"))
        assertTrue(script.contains("GlobalMemoryStatusEx"))
        assertFalse(script.contains("Get-CimInstance"))
    }
    @Test fun parsesResourcesAndMultipleDisks() {
        val snapshot = SshSystemProbe.parse("cpu=12.5\r\nmemoryTotal=8192\r\nmemoryAvailable=2048\r\nuptime=60\r\nsystem=Windows\r\ndisk=10000\t2500\tC:\r\ndisk=20000\t5000\t/mount with spaces\r\n")!!
        assertEquals(12.5, snapshot.cpuPercent!!, 0.01)
        assertEquals(6144L, snapshot.memoryUsedBytes)
        assertEquals(2, snapshot.disks.size)
        assertEquals("/mount with spaces", snapshot.disks[1].name)
        assertEquals(2500L, snapshot.disks[0].usedBytes)
    }

    @Test fun rejectsInvalidMetricsAndIgnoresInvalidDisks() {
        assertNull(SshSystemProbe.parse("cpu=NaN\nmemoryTotal=10\nmemoryAvailable=5\nuptime=1"))
        assertNull(SshSystemProbe.parse("cpu=1\nmemoryTotal=10\nmemoryAvailable=20\nuptime=1"))
        val snapshot = SshSystemProbe.parse("cpu=1\nmemoryTotal=10\nmemoryAvailable=5\nuptime=1\ndisk=0\t0\t/empty")!!
        assertTrue(snapshot.disks.isEmpty())
    }

    @Test fun unavailableCpuKeepsMemoryAndDisks() {
        val snapshot = SshSystemProbe.parse("memoryTotal=100\nmemoryAvailable=40\nuptime=10\ncpu=unavailable\ndisk=200\t100\tC:")!!
        assertNull(snapshot.cpuPercent)
        assertEquals(60L, snapshot.memoryUsedBytes)
        assertEquals(1, snapshot.disks.size)
    }
}
