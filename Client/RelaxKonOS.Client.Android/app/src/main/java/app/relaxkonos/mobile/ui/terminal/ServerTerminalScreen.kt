package app.relaxkonos.mobile.ui.terminal

import android.app.Application
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.InlineTextContent
import androidx.compose.foundation.text.appendInlineContent
import androidx.compose.ui.text.Placeholder
import androidx.compose.ui.text.PlaceholderVerticalAlign
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.selection.DisableSelection
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.ui.platform.LocalClipboard
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.ClipEntry
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.input.key.*
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.IconButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextDirection
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.lifecycle.AndroidViewModel
import androidx.compose.ui.unit.sp
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.RelaxKonApplication
import app.relaxkonos.mobile.core.auth.SessionState
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing
import app.relaxkonos.mobile.ui.theme.TerminalType
import app.relaxkonos.mobile.ui.common.appContainer
import kotlinx.coroutines.launch
import androidx.compose.runtime.rememberCoroutineScope
import androidx.lifecycle.compose.LifecycleStartEffect

/** Retains a selected session across rotation. Leaving the page only detaches its transport. */
class ServerTerminalViewModel(application: Application) : AndroidViewModel(application) {
    private val controller = ServerTerminalController(
        getApplication<RelaxKonApplication>().container.session, viewModelScope,
        nativeResponses = { getApplication<RelaxKonApplication>().container.appearance.terminalType == TerminalType.Native },
    )
    val state = controller.state
    val presentation = TerminalPresentation()
    private val container get() = getApplication<RelaxKonApplication>().container
    private var settingsJob: kotlinx.coroutines.Job? = null
    private var activeOwner: SessionState.Active? = null
    init {
        viewModelScope.launch { container.session.state.collect { state ->
            val owner = state as? SessionState.Active
            if (activeOwner !== owner) {
                settingsJob?.cancel(); activeOwner = owner; presentation.bindOwner(owner)
                controller.resetOwner()
            }
        } }
    }
    fun connect(owner: SessionState.Active) {
        if (activeOwner !== owner) { settingsJob?.cancel(); activeOwner = owner; presentation.bindOwner(owner) }
        controller.connect(owner)
        if (!presentation.settingsVerified && settingsJob?.isActive != true) readSettings(owner)
    }
    fun readSettings(owner: SessionState.Active) {
        if (settingsJob?.isActive == true) return
        presentation.settingsBusy = true
        settingsJob = viewModelScope.launch {
            try {
                val result = container.terminalSettings.read(owner)
                if (activeOwner !== owner) return@launch
                presentation.settingsVerified = result is app.relaxkonos.mobile.core.net.ApiResult.Success
                if (result is app.relaxkonos.mobile.core.net.ApiResult.Success) { presentation.settings = result.value; presentation.settingsMessage = null }
                else presentation.settingsMessage = app.relaxkonos.mobile.ui.common.UiMessage(R.string.terminal_settings_unavailable)
            } finally { if (activeOwner === owner) presentation.settingsBusy = false }
        }
    }
    fun saveSettings(owner: SessionState.Active, value: app.relaxkonos.mobile.core.net.TerminalSettings) {
        if (settingsJob?.isActive == true || !presentation.settingsVerified) return
        val expected = presentation.settings
        presentation.settingsBusy = true
        settingsJob = viewModelScope.launch {
            try {
                val result = container.terminalSettings.save(owner, expected, value)
                if (activeOwner !== owner) return@launch
                when (result) {
                    is app.relaxkonos.mobile.core.net.ApiResult.Success -> {
                        presentation.settings = result.value; presentation.localFontSize = null; presentation.settingsMessage = app.relaxkonos.mobile.ui.common.UiMessage(R.string.terminal_settings_saved)
                    }
                    else -> {
                        presentation.settingsVerified = false
                        presentation.settingsMessage = app.relaxkonos.mobile.ui.common.UiMessage(
                            if (result is app.relaxkonos.mobile.core.net.ApiResult.Problem && result.status < 500) R.string.terminal_settings_changed else R.string.terminal_settings_unknown)
                    }
                }
            } finally { if (activeOwner === owner) presentation.settingsBusy = false }
        }
    }
    fun attach(id: String?) = controller.attach(id)
    fun send(text: String) = controller.send(text)
    fun resize(columns: Int, rows: Int) = controller.resize(columns, rows)
    fun close(sessionId: String) = controller.close(sessionId)
    fun closeSessions(ids: List<String>) = controller.closeSessions(ids)
    fun clearOutput() = controller.clearOutput()
    fun detach() = controller.detach()
    override fun onCleared() { detach(); super.onCleared() }
}

@Composable
fun ServerTerminalScreen(owner: SessionState.Active, modifier: Modifier = Modifier) {
    val model: ServerTerminalViewModel = viewModel()
    val state by model.state.collectAsState()
    LifecycleStartEffect(owner) {
        model.connect(owner)
        onStopOrDispose { model.detach() }
    }
    ServerTerminalContent(owner, state, { model.connect(owner) }, model::attach, model::send,
        model::resize, model::close, model::closeSessions, modifier,
        presentation = model.presentation, onClearOutput = model::clearOutput,
        onReadSettings = { model.readSettings(owner) }, onSaveSettings = { model.saveSettings(owner, it) },
        terminalType = appContainer().appearance.terminalType)
}

@Composable
internal fun ServerTerminalContent(
    owner: SessionState.Active,
    state: ServerTerminalState,
    onConnect: () -> Unit,
    onAttach: (String?) -> Unit,
    onSend: (String) -> Boolean,
    onResize: (Int, Int) -> Unit,
    onCloseSession: (String) -> Unit,
    onCloseSessions: (List<String>) -> Unit,
    modifier: Modifier = Modifier,
    imeInsets: WindowInsets = WindowInsets.ime,
    presentation: TerminalPresentation = remember(owner) { TerminalPresentation().apply { bindOwner(owner) } },
    onClearOutput: () -> Unit = {},
    onReadSettings: () -> Unit = {},
    onSaveSettings: (app.relaxkonos.mobile.core.net.TerminalSettings) -> Unit = {},
    terminalType: TerminalType = TerminalType.Native,
) {
    val input = presentation.input
    val fontSize = presentation.localFontSize ?: presentation.settings.fontSize
    val clipboard = LocalClipboard.current
    val clipboardContext = LocalContext.current
    var textLayout by remember { mutableStateOf<TextLayoutResult?>(null) }
    val matches = remember(state.frame.text, presentation.search) { TerminalInputPolicy.matches(state.frame.text, presentation.search) }
    fun cellSpan(cell: TerminalCellStyle): SpanStyle {
        val normalForeground = cell.foreground?.let { Color(0xff000000L or it.toLong()) } ?: terminalColor(presentation.settings.foregroundColor)
        val normalBackground = cell.background?.let { Color(0xff000000L or it.toLong()) } ?: terminalColor(presentation.settings.backgroundColor)
        val background = if (cell.inverse) normalForeground else normalBackground
        val foreground = if (cell.concealed) background else if (cell.inverse) normalBackground else normalForeground
        val decorations = listOfNotNull(TextDecoration.Underline.takeIf { cell.underline }, TextDecoration.LineThrough.takeIf { cell.strike })
        return SpanStyle(color = foreground.copy(alpha = if (cell.faint) 0.6f else 1f), background = background,
            fontWeight = if (cell.bold) FontWeight.Bold else FontWeight.Normal,
            fontStyle = if (cell.italic) FontStyle.Italic else FontStyle.Normal,
            textDecoration = if (decorations.isEmpty()) TextDecoration.None else TextDecoration.combine(decorations))
    }
    fun searchSpan(index: Int) = SpanStyle(background = if (index == presentation.searchIndex) Color(0xFFFFCC66) else Color(0xFF665500),
        color = if (index == presentation.searchIndex) Color.Black else Color.White)
    val highlighted = remember(state.frame, presentation.settings, matches, presentation.searchIndex) { buildAnnotatedString {
        var offset = 0
        state.frame.glyphs.forEach { glyph ->
            append(state.frame.text.substring(offset, glyph.start))
            appendInlineContent("cell-${glyph.start}", state.frame.text.substring(glyph.start, glyph.end))
            offset = glyph.end
        }
        append(state.frame.text.substring(offset))
        state.frame.styles.forEach { run -> addStyle(cellSpan(run.style), run.start, run.end) }
        matches.forEachIndexed { index, match -> addStyle(searchSpan(index), match.start, match.end) }
    } }
    fun fontChange(delta: Int) { presentation.localFontSize = (fontSize + delta).coerceIn(8.0, 40.0) }
    var menuOpen by remember(owner) { mutableStateOf(false) }
    var closeReview by remember(owner) { mutableStateOf<Pair<List<String>, String>?>(null) }
    val scroll = rememberScrollState()
    val outputHorizontal = rememberScrollState()
    val uiScope = rememberCoroutineScope()
    fun copyOutput() { uiScope.launch {
        runCatching { clipboard.setClipEntry(ClipEntry(android.content.ClipData.newPlainText("Terminal", state.output))) }
            .onFailure { if (presentation.matchesOwner(owner)) presentation.clipboardFailed = true }
    } }
    fun reviewClipboard() { uiScope.launch {
        runCatching {
            val value = clipboard.getClipEntry()?.clipData?.getItemAt(0)?.coerceToText(clipboardContext)?.toString()
            if (presentation.matchesOwner(owner) && (value == null || !presentation.preparePaste(value))) presentation.clipboardFailed = true
        }.onFailure { if (presentation.matchesOwner(owner)) presentation.clipboardFailed = true }
    } }


    val density = LocalDensity.current
    val fontScale = density.fontScale
    val textMeasurer = rememberTextMeasurer()
    val terminalStyle = TextStyle(fontFamily = terminalFont(presentation.settings.fontFamily), fontSize = fontSize.toFloat().sp, lineHeight = (fontSize * 1.5).toFloat().sp, textDirection = TextDirection.Ltr)
    val cellAdvance = textMeasurer.measure("0", terminalStyle).getHorizontalPosition(1, true)
    val cellWidth = cellAdvance / density.density
    // Android fallback CJK/emoji fonts can have a different advance from the Latin monospace font.
    // Fixed-width placeholders keep VT cells aligned while alternate text remains selectable/copyable.
    val inlineCells = remember(state.frame, terminalStyle, cellWidth, fontScale, matches, presentation.settings, presentation.searchIndex) {
        var matchIndex = 0
        state.frame.glyphs.associate { glyph ->
            while (matchIndex < matches.size && matches[matchIndex].end <= glyph.start) matchIndex++
            val match = matches.getOrNull(matchIndex)?.takeIf { it.start < glyph.end }
            val cellStyle = terminalStyle.merge(cellSpan(glyph.style)).let { if (match == null) it else it.merge(searchSpan(matchIndex)) }
            "cell-${glyph.start}" to InlineTextContent(Placeholder((cellWidth * glyph.width / fontScale).sp,
                (fontSize * 1.5).toFloat().sp, PlaceholderVerticalAlign.TextCenter)) {
                Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    DisableSelection { Text(state.frame.text.substring(glyph.start, glyph.end), style = cellStyle, softWrap = false, maxLines = 1,
                        modifier = Modifier.clearAndSetSemantics {}) }
                }
            }
        }
    }

    val decreaseFontLabel = stringResource(R.string.terminal_font_smaller)
    val increaseFontLabel = stringResource(R.string.terminal_font_larger)
    val sessionActionsLabel = stringResource(R.string.terminal_session_actions)
    LaunchedEffect(state.sessionId) { presentation.bindSession(state.sessionId); outputHorizontal.scrollTo(0) }
    LaunchedEffect(matches.size) { presentation.searchIndex = presentation.searchIndex.coerceIn(0, (matches.size - 1).coerceAtLeast(0)) }
    LaunchedEffect(presentation.searchIndex, presentation.search, textLayout) {
        val match = matches.getOrNull(presentation.searchIndex)
        val layout = textLayout
        if (presentation.searchOpen && match != null && layout != null && match.start < layout.layoutInput.text.length) {
            presentation.followOutput = false
            val bounds = layout.getBoundingBox(match.start)
            scroll.scrollTo(bounds.top.toInt().coerceAtLeast(0))
            outputHorizontal.scrollTo(bounds.left.toInt().coerceAtLeast(0))
        }
    }
    LaunchedEffect(scroll) {
        snapshotFlow { scroll.isScrollInProgress to scroll.value }.collect { (scrolling, value) ->
            if (scrolling) presentation.followOutput = value >= scroll.maxValue - 24
        }
    }
    LaunchedEffect(state.output, presentation.followOutput) { if (presentation.followOutput) scroll.scrollTo(scroll.maxValue) }
    fun sendLine() {
        if (!state.canInput) return
        val payload = presentation.payload()
        if (TerminalInputPolicy.needsReview(input)) presentation.prepareDraftReview()
        else if (onSend(payload)) presentation.sent()
    }
    presentation.pasteReview?.let { review -> AlertDialog(
        onDismissRequest = { presentation.pasteReview = null },
        title = { Text(stringResource(R.string.terminal_paste_title)) },
        text = { Column(Modifier.heightIn(max = 320.dp).verticalScroll(rememberScrollState())) { Text(stringResource(R.string.terminal_paste_target, review.sessionId));
            SelectionContainer { Text(TerminalInputPolicy.boundedText(review.payload, 2000)) }
            if (review.payload.length > 2000) Text(stringResource(R.string.terminal_paste_truncated, review.payload.length))
        } },
        confirmButton = { TextButton(enabled = state.canInput && presentation.canPaste(state.sessionId), onClick = {
            if (presentation.canPaste(state.sessionId) && onSend(if (state.frame.bracketedPaste && !review.clearDraft) "\u001b[200~" + review.payload + "\u001b[201~" else review.payload)) presentation.acceptedPaste()
        }) { Text(stringResource(R.string.terminal_send)) } },
        dismissButton = { TextButton(onClick = { presentation.pasteReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    ) }
    if (presentation.settingsOpen) TerminalAppearanceDialog(presentation, onReadSettings, onSaveSettings)
    closeReview?.let { (ids, target) -> AlertDialog(
        onDismissRequest = { closeReview = null },
        title = { Text(stringResource(R.string.terminal_close)) },
        text = { Text(stringResource(R.string.terminal_close_confirm, target), modifier = Modifier.heightIn(max = 320.dp).verticalScroll(rememberScrollState())) },
        confirmButton = { TextButton(enabled = state.connected && !state.busy, onClick = {
            if (ids.size == 1) onCloseSession(ids.single()) else onCloseSessions(ids)
            closeReview = null
        }) { Text(stringResource(R.string.terminal_close)) } },
        dismissButton = { TextButton(onClick = { closeReview = null }) { Text(stringResource(R.string.common_cancel)) } },
    ) }
    TerminalScreenLayout(modifier = modifier, imeInsets = imeInsets, sidebar = {
        Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(Spacing.xs)) {
            Text(stringResource(R.string.terminal_sessions_title), style = MaterialTheme.typography.titleSmall)
            if (!state.connected && !state.connecting) Button(onClick = onConnect) { Text(stringResource(R.string.terminal_reconnect)) }
            else Button(onClick = { onAttach(null) }, enabled = state.connected && !state.busy) { Text(stringResource(R.string.terminal_new)) }
            LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(Spacing.sm)) {
                items(state.sessions, key = { it.sessionId }) { session ->
                    val active = session.sessionId == state.sessionId
                    val stamp = terminalSessionLabel(session.createdAt, session.sessionId, System.currentTimeMillis())
                    TerminalSessionChip(if (active) "${stringResource(R.string.terminal_session_current)} · $stamp" else stamp,
                        active, "${stringResource(R.string.terminal_close)} · $stamp", state.connected && !state.busy,
                        { if (!active) onAttach(session.sessionId) }, { closeReview = listOf(session.sessionId) to stamp }, constrained = true)
                }
            }
        }
    }, header = { compact, wide ->
        // Keep long server addresses to one line so they cannot consume the terminal viewport.
        if (compact) {
            val selectedSession = state.sessions.firstOrNull { it.sessionId == state.sessionId }
            val sessionLabel = selectedSession?.let {
                terminalSessionLabel(it.createdAt, it.sessionId, System.currentTimeMillis())
            }
            Text(listOfNotNull(owner.userName, sessionLabel).joinToString(" · "), style = MaterialTheme.typography.bodySmall,
                maxLines = 1, overflow = TextOverflow.Ellipsis)
        } else Column {
            Text("${owner.userName} · ${owner.effectiveBaseUrl}", style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        if (!compact || !state.connected || state.error || state.sessionLost || state.busy) Text(stringResource(when {
            state.connecting && state.retryAttempt > 0 -> R.string.terminal_recovering
            state.connecting -> R.string.terminal_connecting
            state.busy -> R.string.terminal_switching
            state.error -> R.string.terminal_failed
            state.sessionLost -> R.string.terminal_session_lost
            state.connected -> R.string.terminal_connected
            else -> R.string.terminal_detached
        }), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis,
            color = if (state.error) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
        if (!compact) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(Spacing.xs)) {
                Box(Modifier.weight(1f)) {
                    if (!state.connected && !state.connecting) Button(onClick = { onConnect() }) {
                        Text(stringResource(R.string.terminal_reconnect), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    } else Button(onClick = { onAttach(null) }, enabled = state.connected && !state.busy) {
                        Text(stringResource(R.string.terminal_new), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
                TextButton(onClick = { fontChange(-1) }, enabled = fontSize > 8,
                    contentPadding = PaddingValues(horizontal = Spacing.xs),
                    modifier = Modifier.width(48.dp).semantics { contentDescription = decreaseFontLabel }) { Text("A−") }
                TextButton(onClick = { fontChange(1) }, enabled = fontSize < 40,
                    contentPadding = PaddingValues(horizontal = Spacing.xs),
                    modifier = Modifier.width(48.dp).semantics { contentDescription = increaseFontLabel }) { Text("A+") }
                if (state.sessions.size > 1) Box {
                    TextButton(onClick = { menuOpen = true }, enabled = state.connected && !state.busy,
                        modifier = Modifier.width(48.dp).semantics { contentDescription = sessionActionsLabel }) { Text("⋮") }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        val target = stringResource(R.string.terminal_close_others)
                        DropdownMenuItem(text = { Text(target) }, onClick = {
                            menuOpen = false
                            val targets = state.sessions.filter { it.sessionId != state.sessionId }
                            closeReview = targets.map { it.sessionId } to targets.joinToString("\n") {
                                terminalSessionLabel(it.createdAt, it.sessionId, System.currentTimeMillis())
                            }
                        })
                    }
                }
            }
            if (!wide && state.sessions.isNotEmpty()) {
                // One scrolling row rather than a growing column. `Product.Design.md` gives a phone
                // a single focused session, so switching and pruning must never take rows away from the
                // terminal — which is exactly what a vertical session list did, one session at a time.
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                ) {
                    state.sessions.forEach { session ->
                        val active = session.sessionId == state.sessionId
                        val stamp = terminalSessionLabel(session.createdAt, session.sessionId, System.currentTimeMillis())
                        TerminalSessionChip(
                            label = if (active) "${stringResource(R.string.terminal_session_current)} · $stamp" else stamp,
                            active = active,
                            closeLabel = "${stringResource(R.string.terminal_close)} · $stamp",
                            enabled = state.connected && !state.busy,
                            onSelect = { if (!active) onAttach(session.sessionId) },
                            onClose = { closeReview = listOf(session.sessionId) to stamp },
                        )
                    }
                }
            }
        }
        TerminalOutputToolbar(presentation, state, matches.size, ::copyOutput, ::reviewClipboard, onClearOutput)
        state.exitCode?.let { Text(stringResource(R.string.terminal_exit_code, it), style = MaterialTheme.typography.bodySmall) }
    }, output = {
        if (terminalType == TerminalType.Xterm) XtermTerminal(
            sessionId = state.sessionId.orEmpty(), output = state.rawOutput,
            fontSize = fontSize.toFloat() * fontScale, connected = state.canInput,
            onSend = { onSend(it) }, onResize = onResize, modifier = Modifier.fillMaxSize(),
        ) else
        BoxWithConstraints(Modifier.fillMaxSize()) {
            val width = maxWidth.value
            val height = maxHeight.value
            LaunchedEffect(width, height, fontSize, fontScale, cellWidth) {
                // Text size is in scaled pixels; subtract the actual transcript padding first.
                onResize(((width - 2 * Spacing.md.value) / cellWidth).toInt(),
                    ((height - 2 * Spacing.md.value) / (fontSize * fontScale * 1.5f)).toInt())
            }
            Surface(Modifier.fillMaxSize(), color = terminalColor(presentation.settings.backgroundColor), contentColor = terminalColor(presentation.settings.foregroundColor), shape = MaterialTheme.shapes.medium) {
                Box {
                    SelectionContainer { Column(Modifier.fillMaxSize().verticalScroll(scroll)
                        .horizontalScroll(outputHorizontal).padding(Spacing.md)) {
                        if (state.output.isEmpty() && state.connected && !state.busy && state.sessionId == null)
                            Text(stringResource(R.string.terminal_empty), color = Color(0xFFB7C5D0))
                        else Text(highlighted, inlineContent = inlineCells, style = terminalStyle, softWrap = false, onTextLayout = { textLayout = it },
                            modifier = Modifier.testTag("terminal-output").drawBehind {
                                val layout = textLayout
                                val cursor = state.frame.cursor
                                if (state.canInput && layout != null && cursor != null && cursor < layout.layoutInput.text.length) {
                                    val bounds = layout.getBoundingBox(cursor)
                                    drawRect(terminalColor(presentation.settings.cursorColor), Offset(bounds.left, bounds.top),
                                        Size(bounds.width.coerceAtLeast(cellWidth * density.density * state.frame.cursorWidth), bounds.height), style = Stroke(1.dp.toPx()))
                                }
                            })
                    } }
                    if (!presentation.followOutput && state.output.isNotEmpty()) TextButton(
                        onClick = { presentation.followOutput = true; uiScope.launch { scroll.scrollTo(scroll.maxValue) } },
                        modifier = Modifier.align(Alignment.BottomEnd).background(MaterialTheme.colorScheme.surface, CircleShape),
                    ) { Text(stringResource(R.string.terminal_latest)) }
                }
            }
        }
    }, keys = {
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(Spacing.sm)) {
            OutlinedButton(onClick = { presentation.ctrlNext = !presentation.ctrlNext }, enabled = state.canInput) {
                Text(if (presentation.ctrlNext) "Ctrl ✓" else "Ctrl")
            }
            OutlinedButton(onClick = { presentation.altNext = !presentation.altNext }, enabled = state.canInput) {
                Text(if (presentation.altNext) "Alt ✓" else "Alt")
            }
            TerminalExtendedKeys
                .forEach { (label, key) -> OutlinedButton(onClick = {
                    if (onSend(terminalKeyPayload(key, presentation.ctrlNext, presentation.altNext, applicationCursor = state.frame.applicationCursor))) {
                        presentation.ctrlNext = false; presentation.altNext = false
                    }
                }, enabled = state.canInput) { Text(label) } }
        }
    }, input = { compact ->
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(Spacing.sm), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(value = input, onValueChange = presentation::edit, modifier = Modifier.weight(1f).heightIn(min = 56.dp).onPreviewKeyEvent { event ->
                if (event.type != KeyEventType.KeyDown) false
                else if (event.isCtrlPressed && event.isShiftPressed && event.key == Key.V) { reviewClipboard(); true }
                else if (event.isCtrlPressed && event.isShiftPressed && event.key == Key.C) { copyOutput(); true }
                else if (event.isCtrlPressed && event.isShiftPressed && event.key == Key.F) { presentation.searchOpen = !presentation.searchOpen; true }
                else if (event.key == Key.Enter && !event.isShiftPressed) { sendLine(); true }
                else if (state.canInput && input.isEmpty()) terminalHardwarePayload(event, state.frame.applicationCursor)?.let { onSend(it); true } ?: false
                else false
            },
                label = { Text(stringResource(R.string.terminal_input)) }, enabled = state.canInput,
                maxLines = if (compact) 1 else 3,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send), keyboardActions = KeyboardActions(onSend = { sendLine() }))
            Button(onClick = { sendLine() }, enabled = state.canInput) { Text(stringResource(R.string.terminal_send)) }
        }
    })
}

/**
 * One entry of the session strip.
 *
 * The attached session is both tinted and named — colour alone never carries the state — and every
 * entry closes itself, because a PTY is freed only by an explicit Server call and a phone has no room
 * for a separate screen of session management.
 */
@Composable
private fun TerminalSessionChip(
    label: String,
    active: Boolean,
    closeLabel: String,
    enabled: Boolean,
    onSelect: () -> Unit,
    onClose: () -> Unit,
    constrained: Boolean = false,
) {
    val container = if (active) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceContainerHighest
    val content = if (active) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onSurfaceVariant
    Row(
        modifier = Modifier
            .then(if (constrained) Modifier.fillMaxWidth() else Modifier)
            .clip(RoundedCornerShape(Radius.pill))
            .background(container)
            .clickable(enabled = enabled, role = Role.Tab, onClick = onSelect)
            .semantics { selected = active }
            .padding(start = Spacing.md, end = Spacing.xs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = content, maxLines = 1,
            overflow = TextOverflow.Ellipsis, modifier = if (constrained) Modifier.weight(1f) else Modifier)
        // The glyph is not a word, so the accessible name is set here rather than left as "multiplication sign".
        IconButton(onClick = onClose, enabled = enabled, modifier = Modifier.semantics { contentDescription = closeLabel }) {
            Text("×", style = MaterialTheme.typography.labelMedium, color = content)
        }
    }
}
