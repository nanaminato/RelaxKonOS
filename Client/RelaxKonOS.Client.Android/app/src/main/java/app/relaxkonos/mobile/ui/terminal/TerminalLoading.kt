package app.relaxkonos.mobile.ui.terminal

import androidx.compose.foundation.layout.*
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.theme.Spacing

/** Let the destination paint before loading terminal text/layout or creating an Android renderer. */
@Composable
internal fun TerminalEntry(owner: Any, modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    var ready by remember(owner) { mutableStateOf(false) }
    LaunchedEffect(owner) {
        withFrameNanos { }
        withFrameNanos { }
        ready = true
    }
    Box(modifier) {
        if (ready) content() else TerminalLoading()
    }
}

@Composable
internal fun TerminalLoading(connecting: Boolean = false) {
    Surface(Modifier.fillMaxSize().testTag("terminal-loading"), color = MaterialTheme.colorScheme.surface) {
        Column(Modifier.fillMaxSize().padding(Spacing.lg), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(Spacing.md, Alignment.CenterVertically)) {
            CircularProgressIndicator()
            Text(stringResource(if (connecting) R.string.terminal_connecting else R.string.common_loading))
        }
    }
}
