package app.relaxkonos.mobile.ui.servercenter

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.Modifier
import androidx.compose.material3.Text
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import app.relaxkonos.mobile.R
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.lifecycle.Lifecycle
import org.junit.Rule
import androidx.test.platform.app.InstrumentationRegistry
import android.app.Application
import app.relaxkonos.mobile.servercenter.*
import kotlinx.coroutines.*
import org.junit.Assert.*
import org.junit.After
import org.junit.Test
import java.lang.reflect.Proxy
import kotlin.coroutines.CoroutineContext

class SshFilesControllerTest {
    @get:Rule val rule = createAndroidComposeRule<ComponentActivity>()
    private class QueueDispatcher : CoroutineDispatcher() {
        private val queue = ArrayDeque<Runnable>()
        override fun dispatch(context: CoroutineContext, block: Runnable) { synchronized(queue) { queue.addLast(block) } }
        fun drain() { while (true) { val task = synchronized(queue) { queue.removeFirstOrNull() } ?: return; task.run() } }
    }
    private val dispatcher = QueueDispatcher()
    private val scope = CoroutineScope(SupervisorJob() + dispatcher)
    private var revision = 1
    private var host: String? = "isolated-host"
    private var connects = 0
    private var reads = 0
    private var writes = 0
    private var transferStarts = 0
    private var connectFailure: Exception? = null
    private var failRefreshAfterWrite = false
    private var failFactRead = false
    private val files = mutableMapOf<String, SshFileEntry>()
    private val contents = mutableMapOf<String, ByteArray>()
    private var failAfterSave = false
    private var failClose = false
    private var gate: CompletableDeferred<Unit>? = null
    private val cleanupSessions = mutableSetOf<ServerCenterHostSession>()
    private val secrets = mutableListOf<CharArray>()
    private val target = ServerHostTargetRules.create("isolated.invalid", 22, "review", null, 0)
    private val model by lazy {
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as Application
        SshFilesController(app, { host }, { revision }, { "secret".toCharArray().also { secrets += it } }, { _, _ ->
            connects++
            connectFailure?.let { throw it }
            gate?.let { withContext(NonCancellable) { it.await() } }
            var connected = true
            val transport = Proxy.newProxyInstance(ServerCenterSshTransport::class.java.classLoader,
                arrayOf(ServerCenterSshTransport::class.java)) { _, method, args ->
                when (method.name) {
                    "isConnected" -> connected
                    "listDirectory" -> {
                        reads++
                        if (failRefreshAfterWrite && writes > 0) error("private read failure")
                        files.values.toList()
                    }
                    "fileInfo" -> { if (failFactRead) error("private fact read failure"); files[args!![0] as String] }
                    "download" -> { (args!![1] as java.io.OutputStream).write(requireNotNull(contents[args[0] as String])); Unit }
                    "uploadNew" -> {
                        val path = args!![2] as String; val bytes = (args[0] as java.io.InputStream).readBytes()
                        check(path !in files)
                        writes++; contents[path] = bytes
                        files[path] = SshFileEntry(path, path.substringAfterLast('/'), false, false, bytes.size.toLong())
                        Unit
                    }
                    "upload" -> {
                        val path = args!![2] as String; val bytes = (args[0] as java.io.InputStream).readBytes()
                        writes++; contents[path] = bytes
                        val old = requireNotNull(files[path]); files[path] = old.copy(size = bytes.size.toLong(), modifiedAtEpochMillis = (old.modifiedAtEpochMillis ?: 0) + 1)
                        if (failAfterSave) failFactRead = true
                        Unit
                    }
                    "copyFile" -> {
                        val source = args!![0] as String; val destination = args[1] as String
                        writes++; files[destination] = requireNotNull(files[source]).copy(path = destination, name = destination.substringAfterLast('/'))
                        Unit
                    }
                    "createDirectory" -> {
                        val path = args!![0] as String
                        writes++; files[path] = SshFileEntry(path, path.substringAfterLast('/'), true, false)
                        Unit
                    }
                    "rename" -> {
                        val source = args!![0] as String; val destination = args[1] as String
                        val entry = requireNotNull(files.remove(source))
                        writes++; files[destination] = entry.copy(path = destination, name = destination.substringAfterLast('/'))
                        Unit
                    }
                    "close" -> { if (failClose) error("private close detail"); connected = false; Unit }
                    "toString" -> "Isolated transport"
                    else -> error("Unexpected transport call: " + method.name)
                }
            } as ServerCenterSshTransport
            ServerCenterHostSession(target, transport)
        }, cleanupSessions, { transferStarts++ }, scope)
    }
    @After fun cleanup() { failClose = false; gate?.complete(Unit); model.close(); dispatcher.drain() }
    private fun assertCleared() { assertTrue(secrets.all { secret -> secret.all { it == '\u0000' } }); assertFalse(model.state.value.busy) }
    @Test fun failedCloseStillClearsSecretAndBusyAndRequiresCleanupBeforeNewConnection() {
        failClose = true; model.resume("isolated-host"); dispatcher.drain()
        assertCleared(); assertEquals("cleanup-failed", model.state.value.problem)
        model.beginRename(SshFileEntry("/first", "first", false, false, 1))
        val intent = requireNotNull(model.state.value.nameIntent)
        model.setRename("renamed", intent); model.rename(intent)
        assertEquals("renamed", model.state.value.renameName); assertNotNull(model.state.value.renameTarget)
        model.reload(); dispatcher.drain(); assertEquals(1, connects)
        failClose = false; model.reload(); dispatcher.drain()
        assertEquals(2, connects); assertEquals(2, reads); assertCleared(); assertNull(model.state.value.problem)
    }
    @Test fun cancelledBeforeCoroutineBodyReleasesBusyAndPasswordWithoutConnecting() {
        model.resume("isolated-host"); assertTrue(model.state.value.busy)
        scope.cancel(); dispatcher.drain()
        assertEquals(0, connects); assertCleared(); assertNull(model.state.value.problem)
    }
    @Test fun revisionChangeBeforeConnectionRejectsQueuedRequest() {
        model.resume("isolated-host"); revision++; dispatcher.drain()
        assertEquals(0, connects); assertCleared(); assertNull(model.state.value.problem)
    }
    @Test fun workspaceChangeBeforeConnectionRejectsQueuedRequest() {
        model.resume("isolated-host"); host = "other-host"; dispatcher.drain()
        assertEquals(0, connects); assertCleared(); assertNull(model.state.value.problem)
    }
    @Test fun lateHandshakeAfterStopCannotReadAndStillClearsSecretWhenCloseFails() {
        gate = CompletableDeferred(); model.resume("isolated-host"); dispatcher.drain()
        assertEquals(1, connects); failClose = true; model.stop()
        gate!!.complete(Unit); dispatcher.drain()
        assertEquals(0, reads); assertCleared()
        gate = null; model.resume("isolated-host"); dispatcher.drain()
        assertEquals(1, connects); assertEquals("cleanup-failed", model.state.value.problem)
        failClose = false; model.reload(); dispatcher.drain()
        assertEquals(2, connects); assertCleared()
    }
    @Test fun leavingWithdrawsDeleteAndOldConfirmationCannotConsumeNewTarget() {
        model.resume("isolated-host"); dispatcher.drain()
        val first = SshFileEntry("/first", "first", false, false, 1)
        val second = SshFileEntry("/second", "second", false, false, 1)
        model.askDelete(first); val old = model.state.value.deleteEntries
        model.stop(); assertTrue(model.state.value.deleteEntries.isEmpty())
        model.resume("isolated-host"); dispatcher.drain()
        model.askDelete(second); val current = model.state.value.deleteEntries
        val before = connects
        model.delete(old); dispatcher.drain()
        assertEquals(before, connects); assertSame(current, model.state.value.deleteEntries)
        model.dismissDelete(current); model.delete(current); dispatcher.drain()
        assertEquals(before, connects); assertCleared()
    }

    @Test fun lifecyclePauseWithdrawsDeleteBeforeComposeAndResumeRestartsReading() {
        rule.setContent { rememberSshFilesResumed(model, "isolated-host", revision) { true }; Text("SFTP") }
        lateinit var original: List<SshFileEntry>
        rule.runOnIdle {
            dispatcher.drain()
            model.askDelete(SshFileEntry("/first", "first", false, false, 1))
            original = model.state.value.deleteEntries
            assertEquals(1, connects)
        }
        rule.mainClock.autoAdvance = false
        try {
            rule.activityRule.scenario.moveToState(Lifecycle.State.STARTED)
            rule.runOnUiThread {
                assertTrue(model.state.value.deleteEntries.isEmpty())
                model.delete(original); assertEquals(1, connects)
            }
            rule.activityRule.scenario.moveToState(Lifecycle.State.RESUMED)
        } finally { rule.mainClock.autoAdvance = true }
        rule.runOnIdle { dispatcher.drain(); assertEquals(2, connects); assertCleared() }
    }

    @Test fun disposedPickerKeepsCleanupResponsibilityForAnotherController() {
        failClose = true; model.resume("isolated-host"); dispatcher.drain(); model.close(); dispatcher.drain()
        assertFalse(cleanupSessions.isEmpty()); assertCleared()
        val app = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as Application
        val nextScope = CoroutineScope(SupervisorJob() + dispatcher)
        val next = SshFilesController(app, { host }, { revision }, { "next".toCharArray() },
            { _, _ -> connects++; error("isolated connection refused") }, cleanupSessions, {}, nextScope)
        try {
            next.resume("isolated-host"); dispatcher.drain()
            assertEquals(1, connects); assertEquals("cleanup-failed", next.state.value.problem)
            failClose = false; next.reload(); dispatcher.drain()
            assertTrue(cleanupSessions.isEmpty()); assertEquals(2, connects); assertFalse(next.state.value.busy)
        } finally { failClose = false; next.close(); dispatcher.drain() }
    }

    @Test fun createFailureKeepsDraftAndExplicitRetryWritesOnce() {
        model.resume("isolated-host"); dispatcher.drain(); model.beginCreateDirectory()
        val intent = requireNotNull(model.state.value.nameIntent)
        model.setNewDirectory("draft", intent); connectFailure = IllegalStateException("private authentication detail")
        model.createDirectory(intent); dispatcher.drain()
        assertEquals("draft", model.state.value.newDirectory); assertSame(intent, model.state.value.nameIntent)
        assertEquals("connection-failed", model.state.value.problem); assertFalse(model.state.value.unknown); assertCleared()
        connectFailure = null; model.createDirectory(intent); model.createDirectory(intent); dispatcher.drain()
        assertEquals(1, writes); assertNull(model.state.value.newDirectory); assertNull(model.state.value.nameIntent); assertCleared()
    }
    @Test fun renamePreconditionFailureKeepsNameAndCanRetryExactOriginalTarget() {
        val original = SshFileEntry("/first", "first", false, false, 1)
        model.resume("isolated-host"); dispatcher.drain(); model.beginRename(original)
        val intent = requireNotNull(model.state.value.nameIntent); model.setRename("second", intent)
        model.rename(intent); dispatcher.drain()
        assertEquals("source-changed", model.state.value.problem); assertEquals("second", model.state.value.renameName)
        assertSame(original, model.state.value.renameTarget); assertEquals(0, writes); assertFalse(model.state.value.unknown)
        files[original.path] = original; model.rename(intent); dispatcher.drain()
        assertEquals(1, writes); assertTrue("/second" in files); assertNull(model.state.value.renameTarget); assertCleared()
    }
    @Test fun pausedNameIntentCannotSubmitEditOrCloseRestoredDraft() {
        model.resume("isolated-host"); dispatcher.drain(); model.beginCreateDirectory()
        val old = requireNotNull(model.state.value.nameIntent); model.setNewDirectory("retained", old)
        model.stop(); val before = connects
        model.createDirectory(old); model.setNewDirectory("wrong", old); model.cancelCreateDirectory(old)
        assertEquals(before, connects); assertEquals("retained", model.state.value.newDirectory)
        model.resume("isolated-host"); dispatcher.drain(); val fresh = requireNotNull(model.state.value.nameIntent)
        assertNotSame(old, fresh); model.createDirectory(old); model.cancelCreateDirectory(old)
        assertEquals("retained", model.state.value.newDirectory); assertEquals(0, writes)
        model.createDirectory(fresh); dispatcher.drain(); assertEquals(1, writes); assertCleared()
    }
    @Test fun workspaceChangeDoesNotConsumeRenameDraftOrConnect() {
        model.resume("isolated-host"); dispatcher.drain()
        model.beginRename(SshFileEntry("/first", "first", false, false, 1))
        val intent = requireNotNull(model.state.value.nameIntent); model.setRename("changed", intent)
        revision++; val before = connects
        model.rename(intent); model.cancelRename(intent); model.setRename("wrong", intent)
        assertEquals(before, connects); assertEquals("changed", model.state.value.renameName); assertEquals(0, writes)
    }
    @Test fun failedPostWriteReadKeepsReadOnlyDraftAndCannotReplay() {
        model.resume("isolated-host"); dispatcher.drain(); model.beginCreateDirectory()
        val intent = requireNotNull(model.state.value.nameIntent); model.setNewDirectory("created", intent)
        failRefreshAfterWrite = true; model.createDirectory(intent); dispatcher.drain()
        assertTrue(model.state.value.unknown); assertEquals("created", model.state.value.newDirectory); assertEquals(1, writes)
        model.createDirectory(intent); dispatcher.drain(); assertEquals(1, writes); assertCleared()
    }
    private fun showNameEditor() = rule.setContent {
        val state by model.state.collectAsState()
        MaterialTheme { SshFileNameEditor(state, model) }
    }
    private fun nameAction(id: Int) = requireNotNull(rule.onAllNodesWithText(rule.activity.getString(id))
        .filter(hasClickAction()).onLast().fetchSemanticsNode().config[SemanticsActions.OnClick].action)
    @Test fun productionNameEditorShowsFailureWithDraftAndRetriesWithoutReopening() {
        model.resume("isolated-host"); dispatcher.drain(); model.beginCreateDirectory(); showNameEditor()
        rule.onNode(hasSetTextAction()).performTextInput("visible-draft")
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        rule.runOnIdle { connectFailure = IllegalStateException("private failure") }
        rule.onNodeWithText(rule.activity.getString(R.string.common_save)).performClick()
        rule.runOnIdle { dispatcher.drain() }
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_connection_failed)).assertIsDisplayed()
        rule.onNodeWithText("visible-draft").assertIsDisplayed()
        rule.runOnIdle { connectFailure = null }
        rule.onNodeWithText(rule.activity.getString(R.string.common_save)).performClick()
        rule.runOnIdle { dispatcher.drain(); assertEquals(1, writes) }
        rule.onNodeWithText("visible-draft").assertDoesNotExist()
    }
    @Test fun productionNameEditorReopenRejectsOriginalSubmitAndDismissNodes() {
        model.resume("isolated-host"); dispatcher.drain(); model.beginCreateDirectory(); showNameEditor()
        rule.onNode(hasSetTextAction()).performTextInput("first")
        androidx.test.espresso.Espresso.closeSoftKeyboard()
        val oldSave = nameAction(R.string.common_save); val oldClose = nameAction(R.string.common_cancel)
        rule.runOnIdle {
            model.cancelCreateDirectory(requireNotNull(model.state.value.nameIntent)); model.beginCreateDirectory()
            model.setNewDirectory("second", requireNotNull(model.state.value.nameIntent))
        }
        rule.onNodeWithText("second").assertIsDisplayed()
        rule.runOnIdle { oldSave(); oldClose(); dispatcher.drain(); assertEquals(0, writes); assertEquals("second", model.state.value.newDirectory) }
        rule.onNodeWithText(rule.activity.getString(R.string.common_save)).performClick()
        rule.runOnIdle { dispatcher.drain(); assertEquals(1, writes) }
    }

    private fun preparePaste(cut: Boolean): SshFileEntry {
        val entry = SshFileEntry("/source/first", "first", false, false, 1)
        files[entry.path] = entry
        model.resume("isolated-host"); dispatcher.drain(); model.clipboard(entry, cut)
        model.navigate("/destination"); dispatcher.drain(); model.requestPaste()
        return entry
    }
    @Test fun moveApprovalIsConsumedOnceAndUsesDisplayedDestination() {
        val source = preparePaste(true); val expected = requireNotNull(model.state.value.pasteIntent)
        model.paste(expected); model.paste(expected); dispatcher.drain()
        assertEquals(1, writes); assertEquals(1, transferStarts)
        assertFalse(source.path in files); assertTrue("/destination/first" in files)
        assertNull(model.state.value.clipboard); assertNull(model.state.value.pasteIntent); assertCleared()
    }
    @Test fun copyApprovalRetainsSourceAndClipboardWithoutReplaying() {
        val source = preparePaste(false); val expected = requireNotNull(model.state.value.pasteIntent)
        model.paste(expected); dispatcher.drain(); model.paste(expected); dispatcher.drain()
        assertEquals(1, writes); assertEquals(1, transferStarts)
        assertTrue(source.path in files); assertTrue("/destination/first" in files)
        assertSame(expected.clipboard, model.state.value.clipboard); assertCleared()
    }
    @Test fun recopyingSameEntriesInvalidatesOriginalApprovalAndDismiss() {
        val source = preparePaste(false); val old = requireNotNull(model.state.value.pasteIntent)
        model.clipboard(source, false); model.requestPaste(); val fresh = requireNotNull(model.state.value.pasteIntent)
        val before = connects
        model.paste(old); model.cancelPaste(old); dispatcher.drain()
        assertSame(fresh, model.state.value.pasteIntent); assertEquals(before, connects); assertEquals(0, writes)
        model.paste(fresh); dispatcher.drain(); assertEquals(1, writes)
    }
    @Test fun navigatingInvalidatesPasteApprovalAndCannotUseNewDirectory() {
        preparePaste(false); val old = requireNotNull(model.state.value.pasteIntent)
        model.navigate("/other"); dispatcher.drain(); model.requestPaste()
        val fresh = requireNotNull(model.state.value.pasteIntent); val before = connects
        model.paste(old); model.cancelPaste(old); dispatcher.drain()
        assertSame(fresh, model.state.value.pasteIntent); assertEquals(before, connects); assertEquals(0, writes)
        model.paste(fresh); dispatcher.drain()
        assertTrue("/other/first" in files); assertFalse("/destination/first" in files); assertEquals(1, writes)
    }
    private fun prepareAdopt(): SshAdoptIntent {
        model.resume("isolated-host"); dispatcher.drain(); model.beginCreateDirectory()
        val intent = requireNotNull(model.state.value.nameIntent); model.setNewDirectory("uncertain", intent)
        failRefreshAfterWrite = true; model.createDirectory(intent); dispatcher.drain()
        assertTrue(model.state.value.unknown); assertEquals(1, writes)
        model.checkUnknown(); dispatcher.drain(); model.requestAdopt()
        return requireNotNull(model.state.value.adoptIntent)
    }
    @Test fun equalLookingNewChecksRequireNewAdoptionAndCannotReplayWrite() {
        val old = prepareAdopt(); model.checkUnknown(); dispatcher.drain(); model.requestAdopt()
        val fresh = requireNotNull(model.state.value.adoptIntent)
        assertEquals(old.checks, fresh.checks); assertNotSame(old.checks, fresh.checks)
        model.adoptFacts(old); model.cancelAdopt(old)
        assertTrue(model.state.value.unknown); assertSame(fresh, model.state.value.adoptIntent)
        model.adoptFacts(fresh); model.adoptFacts(fresh)
        assertFalse(model.state.value.unknown); assertNull(model.state.value.adoptIntent); assertEquals(1, writes); assertCleared()
    }
    @Test fun pauseInvalidatesAdoptionAndRequiresAnotherExplicitRead() {
        val old = prepareAdopt(); model.stop(); model.adoptFacts(old)
        assertTrue(model.state.value.unknown); assertTrue(model.state.value.checked.isEmpty())
        failRefreshAfterWrite = false; model.resume("isolated-host"); dispatcher.drain(); model.requestAdopt()
        assertNull(model.state.value.adoptIntent); assertTrue(model.state.value.unknown)
        model.checkUnknown(); dispatcher.drain(); model.requestAdopt()
        val fresh = requireNotNull(model.state.value.adoptIntent)
        model.adoptFacts(old); assertTrue(model.state.value.unknown)
        model.adoptFacts(fresh); assertFalse(model.state.value.unknown); assertEquals(1, writes)
    }
    @Test fun incompleteChecksCannotOfferOrExecuteAdoption() {
        val old = prepareAdopt(); failFactRead = true; model.checkUnknown(); dispatcher.drain(); model.requestAdopt()
        assertNull(model.state.value.adoptIntent); assertTrue(model.state.value.checked.any { it.exists == null })
        model.adoptFacts(old); assertTrue(model.state.value.unknown); assertEquals(1, writes)
    }
    private fun showApprovals() = rule.setContent {
        val state by model.state.collectAsState()
        MaterialTheme { SshFileApprovals(state, model) }
    }
    @Test fun productionPasteApprovalReopenRejectsOriginalConfirmAndCancelNodes() {
        preparePaste(false); showApprovals()
        val old = requireNotNull(model.state.value.pasteIntent)
        rule.onNodeWithText("/source/first").assertIsDisplayed()
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_copy)).assertIsDisplayed()
        val confirm = nameAction(R.string.ssh_files_paste); val cancel = nameAction(R.string.common_cancel)
        lateinit var fresh: SshPasteIntent
        rule.runOnIdle {
            model.cancelPaste(old); model.navigate("/other"); dispatcher.drain(); model.requestPaste()
            fresh = requireNotNull(model.state.value.pasteIntent)
        }
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_paste_confirm, 1, "/other")).assertIsDisplayed()
        rule.runOnIdle { confirm(); cancel(); dispatcher.drain(); assertSame(fresh, model.state.value.pasteIntent); assertEquals(0, writes) }
        rule.onAllNodesWithText(rule.activity.getString(R.string.ssh_files_paste)).filter(hasClickAction()).onLast().performClick()
        rule.runOnIdle { dispatcher.drain(); assertEquals(1, writes); assertTrue("/other/first" in files) }
    }
    @Test fun productionAdoptionApprovalReopenRejectsOriginalConfirmAndCancelNodes() {
        val old = prepareAdopt(); showApprovals()
        val confirm = nameAction(R.string.ssh_files_adopt_facts); val cancel = nameAction(R.string.common_cancel)
        lateinit var fresh: SshAdoptIntent
        rule.runOnIdle { model.cancelAdopt(old); model.checkUnknown(); dispatcher.drain(); model.requestAdopt(); fresh = requireNotNull(model.state.value.adoptIntent) }
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_adopt_note)).assertIsDisplayed()
        rule.runOnIdle { confirm(); cancel(); assertSame(fresh, model.state.value.adoptIntent); assertTrue(model.state.value.unknown) }
        rule.onAllNodesWithText(rule.activity.getString(R.string.ssh_files_adopt_facts)).filter(hasClickAction()).onLast().performClick()
        rule.runOnIdle { assertFalse(model.state.value.unknown); assertEquals(1, writes) }
    }

    @Test fun backgroundTransferFinishesButHiddenPageCannotStartAnotherRequest() {
        val source = preparePaste(false); val expected = requireNotNull(model.state.value.pasteIntent)
        gate = CompletableDeferred(); model.paste(expected); dispatcher.drain()
        assertTrue(model.transferring); model.detach()
        gate!!.complete(Unit); dispatcher.drain(); gate = null
        assertEquals(1, writes); assertFalse(model.state.value.busy)
        val before = connects
        model.requestPaste(); model.reload(); model.askDelete(source); model.requestDownload(source); dispatcher.drain()
        assertNull(model.state.value.pasteIntent); assertTrue(model.state.value.deleteEntries.isEmpty())
        assertNull(model.state.value.downloadIntent); assertEquals(before, connects)
        model.resume("isolated-host"); dispatcher.drain(); model.requestPaste()
        assertNotNull(model.state.value.pasteIntent); assertEquals(1, writes); assertCleared()
    }

    private fun prepareText(path: String = "/first.txt", text: String = "original"): SshDetailIntent {
        val bytes = text.toByteArray(Charsets.UTF_8)
        val entry = SshFileEntry(path, path.substringAfterLast('/'), false, false, bytes.size.toLong(), 1)
        files[path] = entry; contents[path] = bytes
        model.resume("isolated-host"); dispatcher.drain(); model.open(entry); dispatcher.drain()
        assertEquals(text, (model.state.value.preview as SshPreview.Text).content)
        return requireNotNull(model.state.value.detailIntent)
    }
    @Test fun textSavePreconditionFailureKeepsDraftAndRetryWritesOnce() {
        val originalText = "\uFEFFfirst\r\nsecond\r\n"
        val expected = prepareText(text = originalText); val original = requireNotNull(model.state.value.detailEntry)
        val changed = originalText + "next\r\n"
        model.updateText(changed, expected); files[original.path] = original.copy(size = original.size!! + 1)
        model.saveText(expected); dispatcher.drain()
        assertEquals("source-changed", model.state.value.problem); assertEquals(changed, (model.state.value.preview as SshPreview.Text).content)
        assertEquals(0, writes); assertFalse(model.state.value.unknown)
        files[original.path] = original; model.saveText(expected); model.saveText(expected); dispatcher.drain()
        assertEquals(1, writes); assertArrayEquals(changed.toByteArray(Charsets.UTF_8), contents[original.path])
        assertFalse((model.state.value.preview as SshPreview.Text).changed); model.saveText(expected); dispatcher.drain(); assertEquals(1, writes); assertCleared()
    }
    @Test fun dirtyTextCannotBeReplacedByOtherFileNavigationOrFileChanges() {
        val expected = prepareText(); val first = requireNotNull(model.state.value.detailEntry)
        model.clipboard(first, false); model.updateText("retained", expected)
        model.open(SshFileEntry("/other.txt", "other.txt", false, false, 1)); model.navigate("/other")
        model.beginRename(first); model.askDelete(first); model.beginCreateDirectory(); model.requestPaste()
        assertSame(first, model.state.value.detailEntry); assertEquals("/", model.state.value.path)
        assertEquals("retained", (model.state.value.preview as SshPreview.Text).content)
        assertNull(model.state.value.renameTarget); assertNull(model.state.value.newDirectory); assertNull(model.state.value.pasteIntent)
        assertTrue(model.state.value.deleteEntries.isEmpty()); assertEquals(0, writes)
    }
    @Test fun pausedTextTokenCannotModifySaveOrCloseRestoredDraft() {
        val old = prepareText(); model.updateText("retained", old); model.stop()
        model.updateText("wrong", old); model.saveText(old); model.closeDetail(old)
        assertEquals("retained", (model.state.value.preview as SshPreview.Text).content); assertEquals(0, writes)
        model.resume("isolated-host"); dispatcher.drain(); val fresh = requireNotNull(model.state.value.detailIntent)
        assertNotSame(old, fresh); model.updateText("wrong", old); model.saveText(old); model.closeDetail(old)
        assertNull(model.state.value.discardIntent); assertEquals("retained", (model.state.value.preview as SshPreview.Text).content)
        model.saveText(fresh); dispatcher.drain(); assertEquals(1, writes); assertCleared()
    }
    @Test fun discardConfirmationFreezesDraftAndIsConsumedOnce() {
        val detail = prepareText(); model.updateText("retained", detail); model.closeDetail(detail)
        val old = requireNotNull(model.state.value.discardIntent)
        model.updateText("wrong", detail); model.saveText(detail)
        assertEquals("retained", (model.state.value.preview as SshPreview.Text).content); assertEquals(0, writes)
        model.cancelDiscard(old); model.closeDetail(detail); val fresh = requireNotNull(model.state.value.discardIntent)
        model.discardDetail(old); model.cancelDiscard(old); assertSame(fresh, model.state.value.discardIntent)
        model.discardDetail(fresh); model.discardDetail(fresh)
        assertNull(model.state.value.detailEntry); assertNull(model.state.value.discardIntent); assertEquals(SshPreview.None, model.state.value.preview)
    }
    @Test fun originalDetailCallbacksCannotAffectAnotherFile() {
        val old = prepareText(); model.updateText("first draft", old); model.closeDetail(old)
        val discard = requireNotNull(model.state.value.discardIntent); model.discardDetail(discard)
        val fresh = prepareText("/second.txt", "second"); model.updateText("second draft", fresh)
        val before = connects
        model.updateText("wrong", old); model.saveText(old); model.closeDetail(old); model.discardDetail(discard)
        assertEquals(before, connects); assertEquals("/second.txt", model.state.value.detailEntry?.path)
        assertEquals("second draft", (model.state.value.preview as SshPreview.Text).content); assertEquals(0, writes)
        model.saveText(fresh); dispatcher.drain(); assertEquals(1, writes); assertEquals("second draft", contents["/second.txt"]!!.toString(Charsets.UTF_8))
    }
    @Test fun invalidUtf8IsNotOfferedForEditing() {
        val entry = SshFileEntry("/invalid.txt", "invalid.txt", false, false, 2)
        val bytes = byteArrayOf(0xC3.toByte(), 0x28)
        files[entry.path] = entry; contents[entry.path] = bytes
        model.resume("isolated-host"); dispatcher.drain(); model.open(entry); dispatcher.drain()
        assertEquals("text-unreadable", model.state.value.problem); assertEquals(SshPreview.None, model.state.value.preview)
        model.saveText(requireNotNull(model.state.value.detailIntent)); dispatcher.drain()
        assertEquals(0, writes); assertArrayEquals(bytes, contents[entry.path]); assertCleared()
    }
    @Test fun shortenedPreviewReadFailsWithoutCreatingEditableText() {
        val entry = SshFileEntry("/short.txt", "short.txt", false, false, 3)
        files[entry.path] = entry; contents[entry.path] = byteArrayOf(65, 66)
        model.resume("isolated-host"); dispatcher.drain(); model.open(entry); dispatcher.drain()
        assertEquals("source-changed", model.state.value.problem); assertEquals(SshPreview.None, model.state.value.preview); assertEquals(0, writes)
    }
    private fun showTextDetail() = rule.setContent {
        val state by model.state.collectAsState()
        MaterialTheme {
            Column(Modifier.verticalScroll(rememberScrollState())) { SshFileDetailPane(state, model) }
            SshFileDiscardConfirmation(state, model)
        }
    }
    @Test fun postSaveReadFailureKeepsReadonlyDraftAndCannotReplay() {
        val detail = prepareText(); model.updateText("retained", detail); failAfterSave = true
        model.saveText(detail); dispatcher.drain()
        assertTrue(model.state.value.unknown); assertEquals(1, writes)
        model.updateText("wrong", detail); model.saveText(detail); dispatcher.drain()
        assertEquals("retained", (model.state.value.preview as SshPreview.Text).content); assertEquals(1, writes)
        showTextDetail()
        rule.onNodeWithText("retained").performScrollTo().assertIsNotEnabled()
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_save)).performScrollTo().assertIsNotEnabled()
    }
    @Test fun productionTextEditorRejectsOriginalSaveAndDiscardNodes() {
        val first = prepareText(); model.updateText("first draft", first); showTextDetail()
        val oldSave = nameAction(R.string.ssh_files_save)
        rule.runOnIdle { model.closeDetail(first) }
        val oldDiscard = nameAction(R.string.ssh_files_discard); val oldCancel = nameAction(R.string.common_cancel)
        rule.onNodeWithText(rule.activity.getString(R.string.common_cancel)).performClick()
        rule.runOnIdle { model.closeDetail(first) }
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_discard_title)).assertIsDisplayed()
        rule.runOnIdle { oldDiscard(); oldCancel(); assertNotNull(model.state.value.discardIntent); assertEquals("first draft", (model.state.value.preview as SshPreview.Text).content) }
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_discard)).performClick()
        rule.runOnIdle {
            val second = prepareText("/second.txt", "second")
            model.updateText("second draft", second)
        }
        rule.onNodeWithText("second draft").performScrollTo().assertIsDisplayed()
        rule.runOnIdle { oldSave(); oldDiscard(); assertEquals(0, writes); assertEquals("/second.txt", model.state.value.detailEntry?.path) }
        rule.onNodeWithText(rule.activity.getString(R.string.ssh_files_save)).performScrollTo().performClick()
        rule.runOnIdle { dispatcher.drain(); assertEquals(1, writes); assertEquals("second draft", contents["/second.txt"]!!.toString(Charsets.UTF_8)) }
    }

    @Test fun lateDownloadResultCannotConsumeReplacementIntent() {
        model.resume("isolated-host"); dispatcher.drain()
        val first = SshFileEntry("/first", "first", false, false, 1)
        model.requestDownload(first); val old = requireNotNull(model.state.value.downloadIntent)
        model.downloadLaunchHandled(old)
        model.requestDownload(first.copy(path = "/second", name = "second"))
        val current = requireNotNull(model.state.value.downloadIntent); val before = connects
        model.downloadLaunchHandled(old); model.downloadTo(null, old)
        model.downloadTo(android.net.Uri.parse("content://unused/old"), old); dispatcher.drain()
        assertSame(current, model.state.value.downloadIntent); assertTrue(model.state.value.downloadLaunchPending)
        assertEquals(before, connects)
        model.downloadTo(null, current); assertNull(model.state.value.downloadIntent)
    }
    @Test fun downloadIntentSurvivesPickerPauseButNotDirectoryOrWorkspaceChange() {
        model.resume("isolated-host"); dispatcher.drain()
        val entry = SshFileEntry("/first", "first", false, false, 1)
        model.requestDownload(entry); val paused = requireNotNull(model.state.value.downloadIntent)
        model.detach(); model.downloadTo(null, paused); assertSame(paused, model.state.value.downloadIntent)
        model.resume("isolated-host"); dispatcher.drain()
        model.downloadTo(null, paused); assertNull(model.state.value.downloadIntent)
        model.requestDownload(entry); val old = requireNotNull(model.state.value.downloadIntent)
        model.navigate("/other"); dispatcher.drain()
        model.downloadTo(android.net.Uri.parse("content://unused/old"), old); assertNull(model.state.value.downloadIntent)
        model.requestDownload(entry); revision++; model.resume("isolated-host"); dispatcher.drain()
        assertNull(model.state.value.downloadIntent); assertCleared()
    }

    @Test fun lateUploadSelectionOrCancellationCannotConsumeReplacementIntent() {
        model.resume("isolated-host"); dispatcher.drain()
        val old = requireNotNull(model.requestUpload(folder = false))
        val current = requireNotNull(model.requestUpload(folder = false)); val before = connects
        model.upload(old); model.upload(old, listOf(android.net.Uri.parse("content://unused/old")))
        dispatcher.drain(); assertSame(current, model.state.value.uploadIntent); assertEquals(before, connects)
        model.upload(current); assertNull(model.state.value.uploadIntent); assertEquals(before, connects)
    }
    @Test fun uploadSelectionKeepsPickerPauseAndRejectsChangedDirectoryOrWorkspace() {
        model.resume("isolated-host"); dispatcher.drain()
        val expected = requireNotNull(model.requestUpload(folder = true))
        val before = connects
        model.upload(expected, listOf(android.net.Uri.parse("content://unused/wrong-mode")))
        assertSame(expected, model.state.value.uploadIntent); assertEquals(before, connects)
        model.detach(); model.upload(expected); assertSame(expected, model.state.value.uploadIntent)
        assertNull(model.requestUpload(folder = false))
        model.resume("isolated-host"); dispatcher.drain(); model.upload(expected)
        assertNull(model.state.value.uploadIntent)
        val old = requireNotNull(model.requestUpload(folder = false))
        model.navigate("/other"); dispatcher.drain(); val afterNavigation = connects
        model.upload(old, listOf(android.net.Uri.parse("content://unused/old"))); dispatcher.drain()
        assertNull(model.state.value.uploadIntent); assertEquals(afterNavigation, connects)
        val obsolete = requireNotNull(model.requestUpload(folder = true))
        revision++; model.resume("isolated-host"); dispatcher.drain(); val afterWorkspace = connects
        model.upload(obsolete, tree = android.net.Uri.parse("content://unused/tree/old")); dispatcher.drain()
        assertNull(model.state.value.uploadIntent); assertEquals(afterWorkspace, connects); assertCleared()
    }
    @Test fun uploadCannotReplaceDirtyTextOrStartBehindNameEditor() {
        val detail = prepareText(); model.updateText("retained", detail)
        assertNull(model.requestUpload(folder = false)); assertNull(model.requestUpload(folder = true))
        assertEquals("retained", (model.state.value.preview as SshPreview.Text).content)
        model.closeDetail(detail); model.discardDetail(requireNotNull(model.state.value.discardIntent))
        model.beginCreateDirectory(); assertNull(model.requestUpload(folder = false))
        assertEquals(0, writes); assertNull(model.state.value.uploadIntent)
    }
    @Test fun realDocumentUploadUsesCapturedDirectoryAndConsumesResultOnce() {
        val resolver = InstrumentationRegistry.getInstrumentation().targetContext.contentResolver
        val name = "relaxkon-upload-" + java.util.UUID.randomUUID() + ".txt"
        val values = android.content.ContentValues().apply {
            put(android.provider.MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(android.provider.MediaStore.MediaColumns.MIME_TYPE, "text/plain")
            put(android.provider.MediaStore.MediaColumns.RELATIVE_PATH, "Download/RelaxKonReview")
        }
        val uri = requireNotNull(resolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, values))
        try {
            val bytes = "upload body \u4e2d\u6587\n".toByteArray(Charsets.UTF_8)
            requireNotNull(resolver.openOutputStream(uri)).use { it.write(bytes) }
            model.resume("isolated-host"); dispatcher.drain(); model.navigate("/upload-review"); dispatcher.drain()
            val expected = requireNotNull(model.requestUpload(folder = false))
            model.detach(); model.resume("isolated-host"); dispatcher.drain()
            model.upload(expected, listOf(uri)); model.upload(expected, listOf(uri))
            rule.waitUntil(timeoutMillis = 15_000) { dispatcher.drain(); !model.state.value.busy }
            assertNull(model.state.value.problem); assertFalse(model.state.value.unknown)
            assertEquals(1, writes); assertEquals(1, transferStarts)
            assertArrayEquals(bytes, contents["/upload-review/" + name]); assertNull(model.state.value.uploadIntent)
            val before = connects; model.upload(expected, listOf(uri)); dispatcher.drain()
            assertEquals(before, connects); assertEquals(1, writes); assertCleared()
        } finally { resolver.delete(uri, null, null) }
    }

    @Test fun staleDeleteCancellationCannotDismissReplacementOrReopenedIdenticalTarget() {
        model.resume("isolated-host"); dispatcher.drain()
        val first = SshFileEntry("/first/shared.txt", "shared.txt", false, false, 1)
        val second = first.copy(path = "/second/shared.txt")
        model.askDelete(first); val old = model.state.value.deleteEntries
        rule.setContent { MaterialTheme {
            val state by model.state.collectAsState()
            if (state.deleteEntries.isNotEmpty()) SshFileDeleteConfirmation(state.deleteEntries, state.busy, model::delete, model::dismissDelete)
        } }
        val oldCancel = requireNotNull(rule.onNodeWithText(rule.activity.getString(R.string.common_cancel))
            .fetchSemanticsNode().config[SemanticsActions.OnClick].action)
        rule.runOnIdle { model.askDelete(second) }
        rule.onNodeWithText(second.path).assertIsDisplayed()
        rule.runOnIdle {
            val current = model.state.value.deleteEntries
            oldCancel(); model.dismissDelete(old); assertSame(current, model.state.value.deleteEntries)
            model.dismissDelete(current); assertTrue(model.state.value.deleteEntries.isEmpty())
            model.askDelete(first); val reopened = model.state.value.deleteEntries
            oldCancel(); model.dismissDelete(old); assertSame(reopened, model.state.value.deleteEntries)
            assertEquals(0, writes)
        }
        rule.onNodeWithText(first.path).assertIsDisplayed()
        rule.onNodeWithText(rule.activity.getString(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertTrue(model.state.value.deleteEntries.isEmpty()) }
    }

    @Test fun immediateRepeatedFailureShowsFeedbackAfterDismissal() {
        connectFailure = IllegalStateException("private connection failure")
        model.resume("isolated-host"); dispatcher.drain()
        rule.setContent { MaterialTheme {
            val state by model.state.collectAsState()
            SshFilesFeedback(state)
        } }
        val dismiss = rule.activity.getString(R.string.common_dismiss)
        rule.onNodeWithText(dismiss).assertIsDisplayed().performClick()
        rule.onNodeWithText(dismiss).assertDoesNotExist()
        rule.runOnIdle { model.reload(); dispatcher.drain() }
        rule.onNodeWithText(dismiss).assertIsDisplayed()
        rule.onNodeWithText("private connection failure").assertDoesNotExist()
        rule.runOnIdle { assertEquals(2, connects); assertEquals(0, writes); assertCleared() }
    }

    @Test fun systemBackExitsSelectionThenTraversesParentsBeforeWorkspace() {
        model.resume("isolated-host"); dispatcher.drain()
        model.navigate("/history"); dispatcher.drain()
        model.navigate("/first/second"); dispatcher.drain()
        model.selectMode()
        var workspaceBacks = 0
        rule.setContent {
            androidx.activity.compose.BackHandler { workspaceBacks++ }
            val state by model.state.collectAsState()
            SshFilesBackHandler(state, model)
        }
        fun back() { rule.runOnIdle { rule.activity.onBackPressedDispatcher.onBackPressed() }; rule.waitForIdle() }
        back()
        rule.runOnIdle { assertFalse(model.state.value.selecting); assertEquals("/first/second", model.state.value.path); assertEquals(0, workspaceBacks) }
        back()
        rule.runOnIdle { dispatcher.drain(); assertEquals("/first", model.state.value.path); assertEquals(0, workspaceBacks) }
        back()
        rule.runOnIdle { dispatcher.drain(); assertEquals("/", model.state.value.path); assertEquals(0, workspaceBacks) }
        back()
        rule.runOnIdle { assertEquals(1, workspaceBacks); assertEquals(0, writes) }
    }

    @Test fun systemBackRetainsDirtyTextUntilExplicitDiscard() {
        val entry = SshFileEntry("/draft.txt", "draft.txt", false, false, 3)
        files[entry.path] = entry; contents[entry.path] = "old".toByteArray()
        model.resume("isolated-host"); dispatcher.drain()
        model.open(entry); dispatcher.drain()
        model.updateText("unsaved", requireNotNull(model.state.value.detailIntent))
        var workspaceBacks = 0
        rule.setContent { MaterialTheme {
            androidx.activity.compose.BackHandler { workspaceBacks++ }
            val state by model.state.collectAsState()
            SshFilesBackHandler(state, model)
            SshFileDiscardConfirmation(state, model)
        } }
        rule.runOnIdle { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.runOnIdle {
            assertNotNull(model.state.value.discardIntent)
            assertEquals("unsaved", (model.state.value.preview as SshPreview.Text).content)
            assertEquals(0, workspaceBacks); assertEquals(0, writes)
        }
        rule.onNodeWithText(rule.activity.getString(R.string.common_cancel)).performClick()
        rule.runOnIdle { assertNull(model.state.value.discardIntent); assertNotNull(model.state.value.detailEntry) }
        rule.runOnIdle { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.runOnIdle { model.discardDetail(requireNotNull(model.state.value.discardIntent)); assertNull(model.state.value.detailEntry) }
    }

    @Test fun staleBackCannotNavigateHiddenWorkspaceOrCancelActiveTransfer() {
        model.resume("isolated-host"); dispatcher.drain()
        model.navigate("/first/second"); dispatcher.drain()
        model.detach()
        val before = connects
        model.backFromFiles(); dispatcher.drain()
        assertEquals("/first/second", model.state.value.path); assertEquals(before, connects)
        model.resume("isolated-host"); dispatcher.drain()
        val entry = SshFileEntry("/source", "source", false, false, 1)
        files[entry.path] = entry; model.clipboard(entry, false); model.requestPaste()
        val paste = requireNotNull(model.state.value.pasteIntent)
        gate = CompletableDeferred(); model.paste(paste); dispatcher.drain()
        assertTrue(model.transferring)
        val lease = model.transferLease
        model.backFromFiles()
        assertEquals("/first/second", model.state.value.path); assertTrue(model.transferring)
        assertEquals(lease, model.transferLease); assertEquals(0, writes)
        gate!!.complete(Unit); dispatcher.drain()
    }

}
