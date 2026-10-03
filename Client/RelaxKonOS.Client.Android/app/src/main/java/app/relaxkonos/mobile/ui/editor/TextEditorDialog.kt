package app.relaxkonos.mobile.ui.editor

import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import android.app.Application
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.*
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.manage.git.lineDiff
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.Job
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
        ?: (value.text.isNotEmpty() || encoding != "utf-8" || bom)
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
        unknown = false; failed = false; pendingPath = null
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
        launch {
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
    private fun launch(action: suspend () -> Unit) {
        if (busy) return
        val session = owner
        job = viewModelScope.launch { busy = true; failed = false; saved = false; try { action() } finally { if (owner === session) busy = false } }
    }
    fun clear() {
        job?.cancel(); job = null; owner = null; initialPath = null; repositoryId = null
        baseline = null; latest = null; value = TextFieldValue(""); encoding = "utf-8"; bom = false
        destination = ""; busy = false; failed = false; unknown = false; saved = false; pendingPath = null; preferredNewline = null
    }
}

@Composable
fun TextEditorDialog(owner: SessionState.Active, path: String?, repositoryId: String? = null,
    onSaved: (RemoteTextFile) -> Unit = {}, onClose: () -> Unit) {
    val editor: TextEditorViewModel = viewModel(key = "shared-text-editor")
    var confirmClose by remember { mutableStateOf(false) }
    var query by remember { mutableStateOf("") }
    var replacement by remember { mutableStateOf("") }
    var diff by remember { mutableStateOf(false) }
    var formatDialog by remember { mutableStateOf(false) }
    var searchDialog by remember { mutableStateOf(false) }
    var saveAsDialog by remember { mutableStateOf(false) }
    LaunchedEffect(owner, path, repositoryId) { editor.start(owner, path, repositoryId) }
    LaunchedEffect(editor.saved, editor.baseline) { if (editor.saved) editor.baseline?.let(onSaved) }
    fun close() { if (editor.dirty || editor.unknown) confirmClose = true else { editor.clear(); onClose() } }
    Dialog(onDismissRequest = { if (!editor.busy) close() }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().imePadding().padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                ScreenHeader(stringResource(R.string.editor_title), onBack = { if (!editor.busy) close() })
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(editor.baseline?.path ?: stringResource(R.string.editor_new), style = MaterialTheme.typography.titleSmall)
                    OperationMessageDialog(if (editor.busy) null else if (editor.failed) stringResource(R.string.editor_failed) else if (editor.unknown) stringResource(R.string.editor_unknown) else null, tone = if (editor.failed) StatusTone.Danger else StatusTone.Warning)

                    if (editor.saved) Text(stringResource(R.string.editor_saved), color = MaterialTheme.colorScheme.primary)
                    if (!editor.valid) Text(stringResource(R.string.editor_invalid), color = MaterialTheme.colorScheme.error)
                    editor.latest?.let { latest ->
                        Text(stringResource(R.string.editor_conflict), color = MaterialTheme.colorScheme.error)
                        SelectionContainer(Modifier.heightIn(max = 160.dp).verticalScroll(rememberScrollState())) {
                            Text(lineDiff(latest.content, editor.value.text), fontFamily = FontFamily.Monospace)
                        }
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            OutlinedButton(onClick = editor::discardLatest, enabled = !editor.busy) { Text(stringResource(R.string.git_discard_draft)) }
                            OutlinedButton(onClick = editor::compareLatest, enabled = !editor.busy) { Text(stringResource(R.string.git_compare_latest)) }
                        }
                    }
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        TextButton(onClick = { formatDialog = true }, enabled = !editor.busy) { Text(stringResource(R.string.editor_format)) }
                        TextButton(onClick = { searchDialog = true }, enabled = !editor.busy) { Text(stringResource(R.string.editor_search_replace)) }
                        TextButton(onClick = { diff = true }, enabled = !editor.busy) { Text(stringResource(R.string.git_preview_diff)) }
                    }
                    CodeTextField(editor.value, editor::edit, enabled = !editor.busy && (path == null || editor.baseline != null), modifier = Modifier.weight(1f))
                    // A Git editor saves to its repository. Save-as is offered from the Files entry.
                    if (repositoryId == null && editor.baseline == null) OutlinedTextField(editor.destination, { editor.destination = it },
                        label = { Text(stringResource(R.string.editor_destination)) }, singleLine = true, modifier = Modifier.fillMaxWidth(), enabled = !editor.busy)
                }
                FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Button(onClick = { editor.save() }, enabled = !editor.busy && editor.valid && !editor.unknown && editor.latest == null &&
                        (editor.baseline != null && editor.dirty || path == null && editor.destination.isNotBlank())) { Text(stringResource(R.string.git_save)) }
                    if (repositoryId == null && editor.baseline != null) OutlinedButton(onClick = { saveAsDialog = true },
                        enabled = !editor.busy && editor.valid && !editor.unknown && editor.latest == null) { Text(stringResource(R.string.editor_save_as)) }
                    OutlinedButton(onClick = editor::reload, enabled = !editor.busy && (path != null || editor.baseline != null || editor.destination.isNotBlank())) { ActionLabel(R.string.common_refresh) }
                }
            }
        }
        if (formatDialog) EditorToolDialog(stringResource(R.string.editor_format), { formatDialog = false }) {
            Text(stringResource(R.string.editor_limit), style = MaterialTheme.typography.bodySmall)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                TextEditorPolicy.encodings.forEach { format -> FilterChip(editor.encoding == format,
                    onClick = { editor.format(format) }, enabled = !editor.busy, label = { Text(format.uppercase()) }) }
            }
            Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Checkbox(editor.bom, onCheckedChange = editor::setBomEnabled, enabled = !editor.busy && editor.encoding == "utf-8")
                Text("BOM")
            }
            Text(stringResource(R.string.editor_newline, editor.baseline?.newline ?: "none"))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                listOf("lf", "crlf", "cr").forEach { kind -> TextButton(onClick = { editor.newline(kind) }, enabled = !editor.busy) { Text(kind.uppercase()) } }
            }
        }
        if (searchDialog) EditorToolDialog(stringResource(R.string.editor_search_replace), { searchDialog = false }) {
            OutlinedTextField(query, { query = it }, label = { Text(stringResource(R.string.editor_find)) }, modifier = Modifier.fillMaxWidth(), singleLine = true)
            OutlinedTextField(replacement, { replacement = it }, label = { Text(stringResource(R.string.editor_replace)) }, modifier = Modifier.fillMaxWidth(), singleLine = true)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                TextButton(onClick = { editor.find(query); searchDialog = false }, enabled = !editor.busy && query.isNotEmpty()) { Text(stringResource(R.string.editor_find_next)) }
                TextButton(onClick = { editor.replace(query, replacement) }, enabled = !editor.busy && query.isNotEmpty()) { Text(stringResource(R.string.editor_replace_all)) }
            }
        }
        if (diff) EditorToolDialog(stringResource(R.string.git_preview_diff), { diff = false }) {
            SelectionContainer { Text(lineDiff(editor.baseline?.content.orEmpty(), editor.value.text), fontFamily = FontFamily.Monospace) }
        }
        if (saveAsDialog) AlertDialog(onDismissRequest = { if (!editor.busy) saveAsDialog = false },
            title = { Text(stringResource(R.string.editor_save_as)) },
            text = { OutlinedTextField(editor.destination, { editor.destination = it }, label = { Text(stringResource(R.string.editor_destination)) }, singleLine = true, enabled = !editor.busy) },
            confirmButton = { TextButton(onClick = { editor.save(true); saveAsDialog = false },
                enabled = !editor.busy && editor.valid && !editor.unknown && editor.latest == null && editor.destination.isNotBlank()) { Text(stringResource(R.string.editor_save_as)) } },
            dismissButton = { TextButton(onClick = { saveAsDialog = false }, enabled = !editor.busy) { Text(stringResource(R.string.common_cancel)) } })
        if (confirmClose) AlertDialog(onDismissRequest = { confirmClose = false }, title = { Text(stringResource(R.string.editor_unsaved)) },
            text = { Text(stringResource(R.string.editor_unsaved_note)) },
            confirmButton = { TextButton(onClick = { editor.clear(); onClose() }) { Text(stringResource(R.string.editor_discard_changes)) } },
            dismissButton = { TextButton(onClick = { confirmClose = false }) { Text(stringResource(R.string.editor_continue_editing)) } })
    }
}

@Composable
private fun EditorToolDialog(title: String, onClose: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    AlertDialog(onDismissRequest = onClose, title = { Text(title) },
        text = { Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(Spacing.sm), content = content) },
        confirmButton = { TextButton(onClick = onClose) { Text(stringResource(R.string.common_close)) } })
}

@Composable
internal fun CodeTextField(value: TextFieldValue, onValueChange: (TextFieldValue) -> Unit, enabled: Boolean, modifier: Modifier = Modifier.heightIn(min = 240.dp)) {
    val color = MaterialTheme.colorScheme.primary
    OutlinedTextField(value, onValueChange, enabled = enabled,
        modifier = modifier.fillMaxWidth(),
        textStyle = MaterialTheme.typography.bodyMedium.copy(fontFamily = FontFamily.Monospace),
        visualTransformation = remember(color) { SyntaxDisplay(color) })
}

private class SyntaxDisplay(private val color: Color) : VisualTransformation {
    private val tokens = Regex("\"(?:[^\"\\\\]|\\\\.)*\"|'[^'\\n]*'|#[^\\n]*|//[^\\n]*|\\b(?:true|false|null|fun|val|var|class|return|if|else|using|public|private|def|import)\\b")
    override fun filter(text: AnnotatedString): TransformedText {
        val builder = AnnotatedString.Builder(text)
        tokens.findAll(text.text).take(4000).forEach { builder.addStyle(SpanStyle(color = color), it.range.first, it.range.last + 1) }
        return TransformedText(builder.toAnnotatedString(), OffsetMapping.Identity)
    }
}
