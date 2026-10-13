package app.relaxkonos.mobile.data

import org.junit.Assert.*
import org.junit.Test

class FileListSnapshotsTest {
    @Test fun `expiry removes a folder without refreshing its write time on access`() {
        var time = 0L
        val cache = FileListSnapshots<String> { time }
        cache.put("/docs", "old", 1)
        time = 299_999; assertEquals("old", cache.get("/docs"))
        time = 300_000; assertNull(cache.get("/docs"))
    }
    @Test fun `recent use controls bounded eviction`() {
        val cache = FileListSnapshots<Int>()
        repeat(64) { cache.put("/$it", it, 1) }
        cache.get("/0"); cache.put("/64", 64, 1)
        assertEquals(0, cache.get("/0")); assertNull(cache.get("/1"))
    }
    @Test fun `entry budget excludes oversized snapshots and clear removes all owners data`() {
        val cache = FileListSnapshots<String>()
        cache.put("/large", "large", 50_001); assertNull(cache.get("/large"))
        cache.put("/a", "a", 30_000); cache.put("/b", "b", 30_000)
        assertNull(cache.get("/a")); assertEquals("b", cache.get("/b"))
        cache.clear(); assertNull(cache.get("/b"))
    }
}
