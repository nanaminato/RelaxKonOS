package app.relaxkonos.mobile.ui.terminal

import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.R
import kotlinx.coroutines.*
import kotlinx.coroutines.test.*
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class TerminalSettingsControllerTest {
    private fun owner() = SessionState.Active("host", "https://host", "alice", "work", emptySet(), "linux", ExecutionEligibility.Available,
        workspaceId = "11111111-1111-1111-1111-111111111111")

    @Test fun `cancelled read revokes old save eligibility and explicit read recovers`() = runTest {
        val owner = owner(); val p = TerminalPresentation(); var cancel = false; var writes = 0
        val c = TerminalSettingsController(p, backgroundScope, { owner },
            { if (cancel) throw CancellationException("private detail") else ApiResult.Success(TerminalSettings.Default) },
            { _, _, value -> writes++; ApiResult.Success(value) })
        c.bindOwner(owner); c.read(owner); runCurrent(); assertTrue(p.settingsVerified)
        cancel = true; c.read(owner); runCurrent()
        assertFalse(p.settingsBusy); assertFalse(p.settingsVerified); assertEquals(R.string.terminal_settings_unavailable, p.settingsMessage?.resId)
        c.save(owner, TerminalSettings.Default.copy(fontSize = 18.0)); runCurrent(); assertEquals(0, writes)
        cancel = false; c.read(owner); runCurrent(); assertTrue(p.settingsVerified); assertNull(p.settingsMessage)
    }

    @Test fun `cancelled save is unknown and cannot be replayed until read`() = runTest {
        val owner = owner(); val p = TerminalPresentation(); var writes = 0
        val c = TerminalSettingsController(p, backgroundScope, { owner }, { ApiResult.Success(TerminalSettings.Default) },
            { _, _, _ -> writes++; throw CancellationException("private detail") })
        c.bindOwner(owner); c.read(owner); runCurrent()
        c.save(owner, TerminalSettings.Default.copy(fontSize = 18.0)); runCurrent()
        assertFalse(p.settingsBusy); assertFalse(p.settingsVerified); assertEquals(R.string.terminal_settings_unknown, p.settingsMessage?.resId)
        c.save(owner, TerminalSettings.Default.copy(fontSize = 18.0)); runCurrent(); assertEquals(1, writes)
    }

    @Test fun `unexpected provider errors become feedback without keeping verified facts`() = runTest {
        val owner = owner(); val p = TerminalPresentation(); var fail = false
        val c = TerminalSettingsController(p, backgroundScope, { owner },
            { if (fail) error("private detail") else ApiResult.Success(TerminalSettings.Default) },
            { _, _, _ -> error("private write detail") })
        c.bindOwner(owner); c.read(owner); runCurrent()
        c.save(owner, TerminalSettings.Default.copy(fontSize = 18.0)); runCurrent()
        assertFalse(p.settingsVerified); assertFalse(p.settingsBusy); assertNotNull(p.settingsMessage)
        c.read(owner); runCurrent(); fail = true; c.read(owner); runCurrent()
        assertFalse(p.settingsVerified); assertFalse(p.settingsBusy); assertNotNull(p.settingsMessage)
    }

    @Test fun `late old login cannot replace new settings or release new busy state`() = runTest {
        var owner = owner(); val first = owner; val p = TerminalPresentation()
        val oldReply = CompletableDeferred<TerminalSettings>(); val newReply = CompletableDeferred<TerminalSettings>()
        val c = TerminalSettingsController(p, backgroundScope, { owner },
            { active -> ApiResult.Success(withContext(NonCancellable) { if (active === first) oldReply.await() else newReply.await() }) },
            { _, _, value -> ApiResult.Success(value) })
        c.bindOwner(owner); c.read(owner); runCurrent()
        owner = owner(); c.bindOwner(owner); c.read(owner); runCurrent()
        try {
            oldReply.complete(TerminalSettings.Default.copy(fontSize = 25.0)); runCurrent()
            assertTrue(p.settingsBusy); assertFalse(p.settingsVerified); assertEquals(14.0, p.settings.fontSize, 0.0)
            newReply.complete(TerminalSettings.Default.copy(fontSize = 16.0)); runCurrent()
            assertTrue(p.settingsVerified); assertFalse(p.settingsBusy); assertEquals(16.0, p.settings.fontSize, 0.0)
        } finally { oldReply.complete(TerminalSettings.Default); newReply.complete(TerminalSettings.Default) }
    }
}
