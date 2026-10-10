package app.relaxkonos.mobile.ui.editor

import app.relaxkonos.mobile.ui.common.ActionLabel
import app.relaxkonos.mobile.ui.common.CheckboxOption
import app.relaxkonos.mobile.ui.common.StatusTone
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.*
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.compose.ui.window.DialogWindowProvider
import androidx.core.view.WindowCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.manage.git.lineDiff
import app.relaxkonos.mobile.ui.theme.Spacing

@Composable
fun TextEditorDialog(owner: SessionState.Active, path: String?, repositoryId: String? = null,
    onSaved: (RemoteTextFile) -> Unit = {}, onClose: () -> Unit) {
    val container = appContainer()
    val editor: TextEditorViewModel = viewModel(key = "shared-text-editor", factory = viewModelFactory {
        initializer { TextEditorViewModel(container.session, container.textEditor) }
    })
    LaunchedEffect(owner, path, repositoryId) { editor.start(owner, path, repositoryId) }
    TextEditorContent(editor, path, repositoryId, onSaved, onClose)
}

@Composable
internal fun TextEditorContent(editor: TextEditorViewModel, path: String?, repositoryId: String?,
    onSaved: (RemoteTextFile) -> Unit = {}, onClose: () -> Unit) {
    var confirmClose by remember { mutableStateOf(false) }
    var query by remember { mutableStateOf("") }
    var replacement by remember { mutableStateOf("") }
    var diff by remember { mutableStateOf(false) }
    var formatDialog by remember { mutableStateOf(false) }
    var searchDialog by remember { mutableStateOf(false) }
    var saveAsDialog by remember { mutableStateOf(false) }
    LaunchedEffect(editor.saved, editor.baseline) { if (editor.saved) editor.baseline?.let(onSaved) }
    fun close() { if (editor.dirty || editor.unknown) confirmClose = true else { editor.clear(); onClose() } }
    Dialog(onDismissRequest = { if (!editor.busy) close() }, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        EditorSystemBars()
        Surface(Modifier.fillMaxSize()) {
            Column(Modifier.fillMaxSize().safeDrawingPadding().imePadding().padding(Spacing.md), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                ScreenHeader(stringResource(R.string.editor_title), onBack = { if (!editor.busy) close() })
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                    Text(editor.verificationPath ?: editor.baseline?.path ?: stringResource(R.string.editor_new), style = MaterialTheme.typography.titleSmall)
                    OperationMessageDialog(if (editor.busy) null else if (editor.unknown) stringResource(R.string.editor_unknown) else if (editor.failed) stringResource(R.string.editor_failed) else null, tone = if (editor.unknown) StatusTone.Warning else StatusTone.Danger)

                    if (editor.saved) Text(stringResource(R.string.editor_saved), color = MaterialTheme.colorScheme.primary)
                    if (!editor.valid) Text(stringResource(R.string.editor_invalid), color = MaterialTheme.colorScheme.error)
                    editor.latest?.let { latest ->
                        Column(Modifier.heightIn(max = 160.dp).verticalScroll(rememberScrollState()),
                            verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                        Text(stringResource(R.string.editor_conflict), color = MaterialTheme.colorScheme.error)
                        Text(latest.path, style = MaterialTheme.typography.bodySmall)
                        Text(stringResource(R.string.editor_read_format, latest.encoding.uppercase(),
                            stringResource(if (latest.bom) R.string.editor_with_bom else R.string.editor_without_bom)))
                        Text(stringResource(R.string.editor_draft_format, editor.encoding.uppercase(),
                            stringResource(if (editor.bom) R.string.editor_with_bom else R.string.editor_without_bom)))
                        SelectionContainer {
                            Text(lineDiff(latest.content, editor.value.text), fontFamily = FontFamily.Monospace)
                        }
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                            OutlinedButton(onClick = editor::discardLatest, enabled = !editor.busy) { Text(stringResource(R.string.git_discard_draft)) }
                            OutlinedButton(onClick = editor::compareLatest, enabled = !editor.busy) { Text(stringResource(R.string.git_compare_latest)) }
                        }
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
            CheckboxOption(editor.bom, "BOM", !editor.busy && editor.encoding == "utf-8", editor::setBomEnabled)
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
        if (saveAsDialog) AlertDialog(onDismissRequest = { if (!editor.busy) saveAsDialog = false }, modifier = Modifier.imePadding(),
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
internal fun EditorSystemBars() {
    val view = LocalView.current
    val lightSurface = MaterialTheme.colorScheme.surface.luminance() > 0.5f
    SideEffect {
        (view.parent as? DialogWindowProvider)?.window?.let { window ->
            WindowCompat.getInsetsController(window, view).apply {
                isAppearanceLightStatusBars = lightSurface
                isAppearanceLightNavigationBars = lightSurface
            }
        }
    }
}

@Composable
private fun EditorToolDialog(title: String, onClose: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    AlertDialog(onDismissRequest = onClose, modifier = Modifier.imePadding(), title = { Text(title) },
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
