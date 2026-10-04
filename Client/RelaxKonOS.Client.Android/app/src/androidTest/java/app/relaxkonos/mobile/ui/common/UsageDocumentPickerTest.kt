package app.relaxkonos.mobile.ui.common

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.content.Intent
import android.content.SharedPreferences
import android.net.Uri
import android.provider.DocumentsContract
import androidx.activity.result.contract.ActivityResultContracts
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.ExecutionEligibility
import app.relaxkonos.mobile.data.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class UsageDocumentPickerTest {
    private fun owner() = SessionState.Active("test-server", "https://test", "alice", "main", emptySet(), "linux", ExecutionEligibility.Available, workspaceId = "test-workspace")
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test fun selectionRestoresHintAndCancellationKeepsIt() {
        val store = UsageMemoryStore(InMemoryUsageMemoryStorage())
        val owner = owner()
        val scope = store.capture(owner) { owner }
        val uri = Uri.parse("content://test.documents/document/opaque-file-id")
        val contract = UsageDocumentContract(ActivityResultContracts.OpenDocument(), { store.capture(owner) { owner } }, "upload", { it }, { null })
        assertFalse(contract.createIntent(context, arrayOf("*/*")).hasExtra(DocumentsContract.EXTRA_INITIAL_URI))
        assertEquals(uri, contract.parseResult(Activity.RESULT_OK, Intent().setData(uri)))
        val intent = contract.createIntent(context, arrayOf("*/*"))
        @Suppress("DEPRECATION")
        assertEquals(uri, intent.getParcelableExtra<Uri>(DocumentsContract.EXTRA_INITIAL_URI))
        assertNull(contract.parseResult(Activity.RESULT_CANCELED, null))
        assertEquals(uri.toString(), scope.directory("upload", false))
    }

    @Test fun explicitTreeLocationWinsAndChangedAccountDropsResult() {
        val store = UsageMemoryStore(InMemoryUsageMemoryStorage())
        var current: SessionState.Active? = owner()
        store.capture(current) { current }.rememberDirectory("folder", false, "content://test/tree/previous")
        val contract = UsageDocumentContract(ActivityResultContracts.OpenDocumentTree(), { store.capture(current) { current } }, "folder", { it }, { null })
        val explicit = Uri.parse("content://test/tree/explicit")
        val intent = contract.createIntent(context, explicit)
        @Suppress("DEPRECATION")
        assertEquals(explicit, intent.getParcelableExtra<Uri>(DocumentsContract.EXTRA_INITIAL_URI))
        current = current!!.copy(userName = "bob")
        assertNull(contract.parseResult(Activity.RESULT_OK, Intent().setData(explicit)))
        assertNull(store.capture(current) { current }.directory("folder", false))
    }

    @Test fun multiSelectAndSaveHaveIndependentPurposes() {
        val store = UsageMemoryStore(InMemoryUsageMemoryStorage())
        val owner = owner()
        val uri = Uri.parse("content://test/document/file")
        val multi = UsageDocumentContract(ActivityResultContracts.OpenMultipleDocuments(), { store.capture(owner) { owner } }, "upload-many", { it.firstOrNull() }, { emptyList() })
        multi.createIntent(context, arrayOf("*/*"))
        assertEquals(listOf(uri), multi.parseResult(Activity.RESULT_OK, Intent().setData(uri)))
        val save = UsageDocumentContract(ActivityResultContracts.CreateDocument("application/json"), { store.capture(owner) { owner } }, "save-report", { it }, { null })
        val intent = save.createIntent(context, "report.json")
        assertFalse(intent.hasExtra(DocumentsContract.EXTRA_INITIAL_URI))
        save.parseResult(Activity.RESULT_OK, Intent().setData(uri))
        assertEquals(uri.toString(), store.capture(owner) { owner }.directory("save-report", false))
    }

    @Test fun privatePreferencesSurviveRecreationAndClearPersists() {
        // Redirect preferences to an isolated test file; never clear the user's actual defaults.
        val base = context
        val name = "usage-memory-test-" + UUID.randomUUID()
        val isolated = object : ContextWrapper(base) {
            override fun getApplicationContext(): Context = this
            override fun getSharedPreferences(ignored: String, mode: Int): SharedPreferences = base.getSharedPreferences(name, mode)
        }
        try {
            val owner = owner()
            val storage = SharedPreferencesUsageMemoryStorage(isolated)
            val store = UsageMemoryStore(storage)
            store.capture(owner) { owner }.rememberAdministrator("admin")
            assertEquals("admin", UsageMemoryStore(SharedPreferencesUsageMemoryStorage(isolated)).capture(owner) { owner }.administrator)
            store.clear(owner)
            assertNull(UsageMemoryStore(SharedPreferencesUsageMemoryStorage(isolated)).capture(owner) { owner }.administrator)
        } finally { base.deleteSharedPreferences(name) }
    }
}
