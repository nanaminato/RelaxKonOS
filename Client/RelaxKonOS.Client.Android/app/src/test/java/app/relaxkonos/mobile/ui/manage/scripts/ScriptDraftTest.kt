package app.relaxkonos.mobile.ui.manage.scripts

import org.junit.Assert.*
import org.junit.Test

class ScriptDraftTest {
    private fun owner(platform: String) = app.relaxkonos.mobile.core.auth.SessionState.Active(
        "https://example.com", "https://example.com", "alice", "Workspace", emptySet(), platform,
        app.relaxkonos.mobile.core.net.ExecutionEligibility(true, null, false), workspaceId = "workspace")

    @Test fun `blank account resolves to current while account comparison follows the host`() {
        val blank = ScriptDraft(" \t ")
        assertEquals("alice", blank.executionAccount("alice"))
        assertFalse(blank.requiresApproval(owner("linux")))
        assertFalse(blank.requiresApproval(owner("windows")))
        val variant = ScriptDraft(" ALICE ")
        assertEquals("ALICE", variant.executionAccount("alice"))
        assertFalse(variant.requiresApproval(owner("Windows")))
        assertTrue(variant.requiresApproval(owner("linux")))
        assertTrue(ScriptDraft("other").requiresApproval(owner("windows")))
    }

    @Test fun `account validation rejects NUL and leading hyphen while preserving default semantics`() {
        assertTrue(ScriptDraft("").validRunAs)
        assertTrue(ScriptDraft("domain\\alice").validRunAs)
        assertFalse(ScriptDraft(" \t-root").validRunAs)
        assertFalse(ScriptDraft("alice\u0000").validRunAs)
    }

    @Test fun `paths retain meaningful spaces but blank and NUL paths cannot run`() {
        val paths = ScriptDraft("alice", executable = "/bin/job ", directory = "/work ")
        assertTrue(paths.validPaths)
        assertEquals("/bin/job ", paths.executable)
        assertEquals("/work ", paths.directory)
        assertFalse(paths.copy(executable = " ").validPaths)
        assertFalse(paths.copy(directory = "\t").validPaths)
        assertFalse(paths.copy(executable = "/bin/job\u0000").validPaths)
        assertFalse(paths.copy(directory = "/work\u0000").validPaths)
    }

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
