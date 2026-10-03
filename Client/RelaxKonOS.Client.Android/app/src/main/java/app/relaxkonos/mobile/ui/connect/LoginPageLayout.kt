package app.relaxkonos.mobile.ui.connect

import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import app.relaxkonos.mobile.ui.theme.Spacing

/** A bounded, keyboard-aware viewport; the form may grow beyond the screen when SSH options expand. */
@Composable
internal fun LoginPageLayout(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    LazyColumn(
        modifier = modifier.fillMaxSize().safeDrawingPadding().imePadding().testTag("login-page-scroll"),
        state = rememberLazyListState(),
        contentPadding = PaddingValues(Spacing.lg),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        item(key = "login-form") { content() }
    }
}
