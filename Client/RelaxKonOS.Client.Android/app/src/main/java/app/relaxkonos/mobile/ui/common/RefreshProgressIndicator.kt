package app.relaxkonos.mobile.ui.common

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

/** Reserve feedback space so polling never moves the page content. */
@Composable
fun RefreshProgressIndicator(visible: Boolean, modifier: Modifier = Modifier) {
    Box(modifier.fillMaxWidth().height(4.dp)) {
        if (visible) LinearProgressIndicator(Modifier.fillMaxWidth())
    }
}
