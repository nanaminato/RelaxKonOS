package app.relaxkonos.mobile.ui.common

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import androidx.activity.result.contract.ActivityResultContract
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import app.relaxkonos.mobile.data.UsageMemoryScope

/** SAF document identifiers are opaque. Pass the last document/tree URI as a location hint. */
internal class UsageDocumentContract<I, O>(
    private val delegate: ActivityResultContract<I, O>,
    private val capture: () -> UsageMemoryScope,
    private val purpose: String,
    private val uriOf: (O) -> Uri?,
    private val cancelled: () -> O,
) : ActivityResultContract<I, O>() {
    private var pending: UsageMemoryScope? = null
    override fun createIntent(context: Context, input: I): Intent {
        val memory = capture().also { pending = it }
        return delegate.createIntent(context, input).apply {
            if (!hasExtra(DocumentsContract.EXTRA_INITIAL_URI)) {
                memory.directory(purpose, false)?.let { saved ->
                    val uri = Uri.parse(saved)
                    if (uri.scheme == "content") putExtra(DocumentsContract.EXTRA_INITIAL_URI, uri)
                }
            }
        }
    }
    override fun parseResult(resultCode: Int, intent: Intent?): O {
        val memory = pending.also { pending = null } ?: return cancelled()
        if (!memory.isCurrent) return cancelled()
        val result = delegate.parseResult(resultCode, intent)
        uriOf(result)?.let { memory.rememberDirectory(purpose, false, it.toString()) }
        return result
    }
}

@Composable
private fun <I, O> rememberUsageContract(delegate: ActivityResultContract<I, O>, purpose: String, uriOf: (O) -> Uri?, cancelled: () -> O): ActivityResultContract<I, O> {
    val container = appContainer()
    return remember(container, purpose) {
        UsageDocumentContract(delegate, { container.usageMemory.capture(container.activeSession) { container.activeSession } }, purpose, uriOf, cancelled)
    }
}

@Composable
fun rememberUsageOpenDocument(purpose: String): ActivityResultContract<Array<String>, Uri?> =
    rememberUsageContract(ActivityResultContracts.OpenDocument(), purpose, { it }, { null })

@Composable
fun rememberUsageOpenMultipleDocuments(purpose: String): ActivityResultContract<Array<String>, List<Uri>> =
    rememberUsageContract(ActivityResultContracts.OpenMultipleDocuments(), purpose, { it.firstOrNull() }, { emptyList() })

@Composable
fun rememberUsageOpenDocumentTree(purpose: String): ActivityResultContract<Uri?, Uri?> =
    rememberUsageContract(ActivityResultContracts.OpenDocumentTree(), purpose, { it }, { null })

@Composable
fun rememberUsageCreateDocument(mimeType: String, purpose: String): ActivityResultContract<String, Uri?> =
    rememberUsageContract(ActivityResultContracts.CreateDocument(mimeType), purpose, { it }, { null })
