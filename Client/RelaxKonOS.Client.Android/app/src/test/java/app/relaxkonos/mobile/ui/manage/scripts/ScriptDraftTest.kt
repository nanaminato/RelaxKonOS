package app.relaxkonos.mobile.ui.manage.scripts

import org.junit.Assert.*
import org.junit.Test

class ScriptDraftTest {
    @Test fun `one empty argument differs from no arguments and multiline values remain intact`() {
        val draft = ScriptDraft("alice", arguments = listOf("", " spaced ", "first\nsecond"))
        assertTrue(draft.validArguments)
        assertTrue(ScriptDraft("alice", arguments = listOf("")).dirty("alice"))
        assertEquals(listOf("", " spaced ", "first\nsecond"), draft.arguments)
        assertFalse(draft.copy(arguments = emptyList()).dirty("alice"))
    }
    @Test fun `argument count and UTF16 lengths accept limits and reject excess and NUL`() {
        assertTrue(ScriptDraft("alice", arguments = List(64) { "" }).validArguments)
        assertFalse(ScriptDraft("alice", arguments = List(65) { "" }).validArguments)
        assertTrue(ScriptDraft("alice", arguments = listOf("x".repeat(4096))).validArguments)
        assertFalse(ScriptDraft("alice", arguments = listOf("x".repeat(4097))).validArguments)
        assertFalse(ScriptDraft("alice", arguments = listOf("a\u0000b")).validArguments)
        assertTrue(ScriptDraft("alice", arguments = listOf("😀".repeat(2048))).validArguments)
        assertFalse(ScriptDraft("alice", arguments = listOf("😀".repeat(2049))).validArguments)
    }
}
