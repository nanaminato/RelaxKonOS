package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.sp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.theme.Spacing
import kotlinx.coroutines.launch

/** Selectable Compose transcript, using the same VT parser as the Server terminal. */
@Composable
internal fun NativeTerminal(sessionId: String, output: String, fontSize: Int,
    onResize: (Int, Int) -> Unit, modifier: Modifier = Modifier, connected: Boolean = true,
    onZoom: ((Int) -> Unit)? = null) {
    val scroll = rememberScrollState()
    val scope = rememberCoroutineScope()
    var follow by remember(sessionId) { mutableStateOf(true) }
    val scale = LocalDensity.current.fontScale
    val zoomStep by rememberUpdatedState(onZoom)
    val tracker = remember { PinchZoomTracker() }
    // Observe two-finger spans in the initial pass: the text keeps its own scroll and selection, and
    // the pinch itself is never consumed. Font changes are applied by the caller after a step.
    val pinch = if (onZoom == null) Modifier else Modifier.pointerInput(Unit) {
        awaitEachGesture {
            awaitFirstDown(requireUnconsumed = false)
            var tracking = false
            while (true) {
                val event = awaitPointerEvent(PointerEventPass.Initial)
                val pressed = event.changes.filter { it.pressed }
                if (pressed.size >= 2) {
                    val span = (pressed[0].position - pressed[1].position).getDistance()
                    if (!tracking) {
                        tracker.start(span); tracking = true
                    } else {
                        val step = tracker.update(span)
                        if (step != 0) zoomStep?.invoke(step)
                    }
                } else if (tracking) {
                    tracker.reset(); tracking = false
                }
                if (event.changes.none { it.pressed }) break
            }
            tracker.reset()
        }
    }
    LaunchedEffect(scroll) {
        snapshotFlow { scroll.isScrollInProgress to scroll.value }.collect { (scrolling, value) ->
            if (scrolling) follow = value >= scroll.maxValue - 24
        }
    }
    LaunchedEffect(output, follow) { if (follow) scroll.scrollTo(scroll.maxValue) }
    BoxWithConstraints(modifier.fillMaxSize().then(pinch)) {
        val width = maxWidth.value
        val height = maxHeight.value
        LaunchedEffect(sessionId, width, height, fontSize, scale, connected) {
            onResize(((width - 2 * Spacing.md.value) / (fontSize * scale * 0.61f)).toInt().coerceIn(20, 300),
                ((height - 2 * Spacing.md.value) / (fontSize * scale * 1.5f)).toInt().coerceIn(5, 100))
        }
        Surface(Modifier.fillMaxSize(), color = Color(0xFF101820), contentColor = Color(0xFFF2F5F7),
            shape = MaterialTheme.shapes.medium) {
            Box {
                SelectionContainer {
                    Text(output, Modifier.fillMaxSize().verticalScroll(scroll)
                        .horizontalScroll(rememberScrollState()).padding(Spacing.md),
                        fontFamily = FontFamily.Monospace, fontSize = fontSize.sp,
                        lineHeight = (fontSize * 1.5f).sp, softWrap = false)
                }
                if (!follow && output.isNotEmpty()) TextButton(
                    onClick = { follow = true; scope.launch { scroll.scrollTo(scroll.maxValue) } },
                    modifier = Modifier.align(Alignment.BottomEnd),
                ) { Text(stringResource(R.string.terminal_latest)) }
            }
        }
    }
}
