package app.relaxkonos.mobile.ui.editor

import android.app.Application
import androidx.compose.runtime.*
import androidx.compose.ui.text.input.*
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import kotlinx.coroutines.Job
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
class TextEditorViewModel(application: Application) : AndroidViewModel(application) {
    private val client get() = getApplication<RelaxKonApplication>().container.textEditor
    private var owner: SessionState.Active? = null
    private var repositoryId: String? = null
    private var initialPath: String? = null
    private var job: Job? = null
    private var pendingPath: String? = null
    private var preferredNewline: String? = null
    var baseline by mutableStateOf<RemoteTextFile?>(null); private set
    var latest by mutableStateOf<RemoteTextFile?>(null); private set
    var value by mutableStateOf(TextFieldValue("")); private set
    var encoding by mutableStateOf("utf-8"); private set
    var bom by mutableStateOf(false); private set
    var destination by mutableStateOf("")
    var busy by mutableStateOf(false); private set
    var failed by mutableStateOf(false); private set
    var unknown by mutableStateOf(false); private set
    var saved by mutableStateOf(false); private set
    init {
        viewModelScope.launch {
            getApplication<RelaxKonApplication>().container.session.state.collect { current ->
                if (owner != null && current !== owner) clear()
            }
        }
    }
    val dirty get() = baseline?.let { value.text != it.content || encoding != it.encoding || bom != it.bom }
        ?: (value.text.isNotEmpty() || encoding != "utf-8" || bom || destination.isNotEmpty())
    val valid get() = TextEditorPolicy.valid(value.text, encoding, bom)

    fun start(session: SessionState.Active, path: String?, gitId: String?) {
        if (owner === session && initialPath == path && repositoryId == gitId) return
        clear(); owner = session; initialPath = path; repositoryId = gitId
        if (path != null) reload()
    }
    fun edit(next: TextFieldValue) { value = preserveInsertedNewlines(value, next, preferredNewline); saved = false }
    fun format(name: String) { encoding = name; if (name != "utf-8") bom = true; saved = false }
    fun setBomEnabled(enabled: Boolean) { bom = enabled; saved = false }
    fun find(query: String) {
        if (query.isEmpty()) return
        val index = value.text.indexOf(query, value.selection.end).takeIf { it >= 0 } ?: value.text.indexOf(query)
        if (index >= 0) value = value.copy(selection = androidx.compose.ui.text.TextRange(index, index + query.length))
    }
    fun replace(query: String, replacement: String) {
        if (query.isEmpty()) return
        val separator = when (preferredNewline) { "crlf" -> "\r\n"; "cr" -> "\r"; else -> "\n" }
        val insert = replacement.replace("\r\n", "\n").replace('\r', '\n').replace("\n", separator)
        value = TextFieldValue(value.text.replace(query, insert)); saved = false
    }
    fun newline(kind: String) {
        preferredNewline = kind
        val separator = when (kind) { "crlf" -> "\r\n"; "cr" -> "\r"; else -> "\n" }
        value = TextFieldValue(value.text.replace("\r\n", "\n").replace('\r', '\n').replace("\n", separator)); saved = false
    }
    private fun accept(file: RemoteTextFile) {
        baseline = file; latest = null; value = TextFieldValue(file.content); encoding = file.encoding; bom = file.bom
        preferredNewline = file.newline
        unknown = false; failed = false; pendingPath = null; destination = ""
    }
    fun reload() {
        val session = owner ?: return
        val path = pendingPath ?: baseline?.path ?: initialPath ?: destination.takeIf { it.isNotBlank() } ?: return
        launch {
            when (val result = client.read(session, path, repositoryId)) {
                is ApiResult.Success -> {
                    if (baseline != null && dirty || unknown) {
                        if (result.value.content == value.text && result.value.encoding == encoding && result.value.bom == bom) {
                            accept(result.value); saved = true
                        } else latest = result.value
                    } else accept(result.value)
                }
                else -> failed = true
            }
        }
    }
    fun discardLatest() { latest?.let(::accept) }
    fun compareLatest() { latest?.let { baseline = it; latest = null; unknown = false; failed = false; pendingPath = null } }
    fun save(asNew: Boolean = false) {
        val session = owner ?: return
        if (busy || !valid || unknown || latest != null) return
        val opened = baseline
        val content = value.text
        val format = opened?.copy(encoding = encoding, bom = bom)
        val target = destination.trim()
        val formatEncoding = encoding
        val formatBom = bom
        if ((opened == null || asNew) && target.isBlank()) return
        pendingPath = if (opened == null || asNew) target else opened.path
        launch(write = true) {
            val result = if (opened == null || asNew) client.create(session, target, content, formatEncoding, formatBom)
                else client.save(session, requireNotNull(format), content, repositoryId)
            when (result) {
                is ApiResult.Success -> { accept(result.value); saved = true; if (asNew) repositoryId = null }
                is ApiResult.Problem -> {
                    failed = true
                    if (result.status >= 500) unknown = true
                    if (result.status == 409 && opened != null && !asNew) {
                        when (val current = client.read(session, opened.path, repositoryId)) {
                            is ApiResult.Success -> latest = current.value
                            else -> unknown = true
                        }
                    }
                }
                is ApiResult.Transport -> { failed = true; unknown = true }
            }
        }
    }
    private fun launch(write: Boolean = false, action: suspend () -> Unit) {
        if (busy) return
        val session = owner
        job = viewModelScope.launch {
            busy = true; failed = false; saved = false
            try { action() }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (_: Exception) { if (owner === session) { failed = true; if (write) unknown = true } }
            finally { if (owner === session) busy = false }
        }
    }
    fun clear() {
        job?.cancel(); job = null; owner = null; initialPath = null; repositoryId = null
        baseline = null; latest = null; value = TextFieldValue(""); encoding = "utf-8"; bom = false
        destination = ""; busy = false; failed = false; unknown = false; saved = false; pendingPath = null; preferredNewline = null
    }
}
