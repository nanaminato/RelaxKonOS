package app.relaxkonos.mobile.core.net

import org.junit.Assert.*
import org.junit.Test

class CloudBrowserWireTest {
    private val first = "10000000-0000-0000-0000-000000000001"
    private val second = "10000000-0000-0000-0000-000000000002"
    @Test fun accountIdentityIsPartOfEveryRoute() {
        assertNotEquals(CloudBrowserWire.entries(first, "same", null), CloudBrowserWire.entries(second, "same", null))
        assertNotEquals(CloudBrowserWire.download(first, "same"), CloudBrowserWire.download(second, "same"))
        assertTrue(CloudBrowserWire.connection(first).endsWith(first))
    }
    @Test fun opaqueReferencesAndPaginationStayInQueryValues() {
        val route = CloudBrowserWire.entries(first, "a&reference=b/#?", "x&cursor=y")
        assertTrue(route.endsWith("?reference=a%26reference%3Db%2F%23%3F&cursor=x%26cursor%3Dy"))
    }
    @Test fun parsesDirectoriesAndContinuationWithoutInventingPaths() {
        val result = CloudBrowserWire.directory("""{"entries":[{"reference":"opaque","name":"same","isDirectory":true,"size":null}],"nextCursor":"next"}""")
        assertEquals("opaque", result.entries.single().reference)
        assertNull(result.entries.single().size)
        assertEquals("next", result.nextCursor)
    }
    @Test(expected = IllegalArgumentException::class) fun rejectsInvalidConnectionIdentity() {
        CloudBrowserWire.download("../other", "file")
    }
}
