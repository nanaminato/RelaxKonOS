package app.relaxkonos.mobile.ui.more

import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.problemMessage

internal fun proxyProblem(code: String): UiMessage = when (code) {
    "docker.proxy.problem.stored_value_unreadable" -> UiMessage(R.string.proxy_problem_stored_value_unreadable)
    "docker.proxy.problem.managed_proxy_unavailable" -> UiMessage(R.string.proxy_problem_managed_proxy_unavailable)
    "docker.proxy.problem.configuration_invalid" -> UiMessage(R.string.proxy_problem_configuration_invalid)
    "docker.proxy.problem.confirmation_required" -> UiMessage(R.string.proxy_problem_confirmation_required)
    "docker.proxy.problem.platform_unsupported" -> UiMessage(R.string.proxy_problem_platform_unsupported)
    "docker.proxy.problem.helper_unavailable" -> UiMessage(R.string.proxy_problem_helper_unavailable)
    "docker.proxy.problem.engine_apply_failed" -> UiMessage(R.string.proxy_problem_engine_apply_failed)
    "docker.proxy.problem.engine_not_applied" -> UiMessage(R.string.proxy_problem_engine_not_applied)
    "docker.proxy.problem.desktop_settings_not_found" -> UiMessage(R.string.proxy_problem_desktop_settings_not_found)
    "docker.proxy.problem.desktop_settings_invalid" -> UiMessage(R.string.proxy_problem_desktop_settings_invalid)
    "docker.proxy.problem.desktop_settings_unreadable" -> UiMessage(R.string.proxy_problem_desktop_settings_unreadable)
    "docker.proxy.problem.desktop_settings_locked" -> UiMessage(R.string.proxy_problem_desktop_settings_locked)
    "docker.proxy.problem.desktop_settings_denied" -> UiMessage(R.string.proxy_problem_desktop_settings_denied)
    "docker.proxy.problem.desktop_stop_failed" -> UiMessage(R.string.proxy_problem_desktop_stop_failed)
    "docker.proxy.problem.desktop_settings_not_applied" -> UiMessage(R.string.proxy_problem_desktop_settings_not_applied)
    "docker.proxy.problem.daemon_unavailable" -> UiMessage(R.string.proxy_problem_daemon_unavailable)
    else -> problemMessage(code)
}

internal fun proxyDetail(code: String): UiMessage = UiMessage(when (code) {
    "docker.proxy.detail.restart_pending" -> R.string.proxy_detail_restart_pending
    "docker.proxy.detail.desktop_restart_pending" -> R.string.proxy_detail_desktop_restart_pending
    "docker.proxy.detail.desktop_upstream_differs" -> R.string.proxy_detail_desktop_upstream_differs
    "docker.proxy.detail.build_only" -> R.string.proxy_detail_build_only
    "docker.proxy.detail.build_loopback_unreachable" -> R.string.proxy_detail_build_loopback_unreachable
    else -> R.string.error_generic
})
