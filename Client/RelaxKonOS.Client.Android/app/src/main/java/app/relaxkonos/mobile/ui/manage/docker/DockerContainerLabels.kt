package app.relaxkonos.mobile.ui.manage.docker

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R

@Composable
internal fun dockerContainerStatus(state: String, status: String): String {
    val label = when (state.lowercase()) {
        "created" -> stringResource(R.string.docker_container_created)
        "running" -> stringResource(R.string.docker_container_running)
        "paused" -> stringResource(R.string.docker_container_paused)
        "restarting" -> stringResource(R.string.docker_container_restarting)
        "removing" -> stringResource(R.string.docker_container_removing)
        "exited" -> stringResource(R.string.docker_container_exited)
        "dead" -> stringResource(R.string.docker_container_dead)
        else -> stringResource(R.string.docker_container_unknown, state)
    }
    return listOfNotNull(label, status.takeIf { it.isNotBlank() && !it.equals(state, ignoreCase = true) }).joinToString(" · ")
}
