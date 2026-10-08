package app.relaxkonos.mobile.ui.manage.scripts

import org.junit.Assert.*
import org.junit.Test

class ScriptEnvironmentTest {
    @Test fun `values preserve spaces empty text and equals without replacing duplicates`() {
        assertEquals(linkedMapOf("FIRST" to " spaced = value ", "EMPTY" to "", "_key1" to "ok"),
            scriptEnvironment("FIRST= spaced = value \nEMPTY=\n_key1=ok\n"))
        assertNull(scriptEnvironment("TOKEN=first\nTOKEN=second"))
        assertEquals(mapOf("TOKEN" to "first", "token" to "second"), scriptEnvironment("TOKEN=first\ntoken=second"))
    }
    @Test fun `count key and value limits accept boundary and reject excess`() {
        assertEquals(32, scriptEnvironment((1..32).joinToString("\n") { "K$it=value" })?.size)
        assertNull(scriptEnvironment((1..33).joinToString("\n") { "K$it=value" }))
        assertNotNull(scriptEnvironment("${"K".repeat(128)}=${"v".repeat(4096)}"))
        assertNull(scriptEnvironment("${"K".repeat(129)}=value"))
        assertNull(scriptEnvironment("K=${"v".repeat(4097)}"))
    }
    @Test fun `invalid syntax and NUL are rejected while blank separators are allowed`() {
        for (entry in listOf("missing", "=value", "1KEY=value", "KEY-NAME=value", "KEY =value", "KEY=v\u0000alue"))
            assertNull(scriptEnvironment(entry))
        assertEquals(emptyMap<String, String>(), scriptEnvironment("\n \r\n"))
        assertEquals(mapOf("KEY" to "value"), scriptEnvironment("\r\nKEY=value\r\n"))
    }
}
