package app.relaxkonos.mobile.ui.manage.proxyserver

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.ProxyServerExportFormats
import app.relaxkonos.mobile.core.net.ProxyServerProtocol
import app.relaxkonos.mobile.core.net.ProxyServerStatus

@Composable internal fun proxyServerProtocolLabel(protocol: ProxyServerProtocol) = stringResource(when (protocol) {
    ProxyServerProtocol.VlessReality -> R.string.proxy_server_protocol_vless_reality
    ProxyServerProtocol.VlessTls -> R.string.proxy_server_protocol_vless_tls
    ProxyServerProtocol.VlessWebSocket -> R.string.proxy_server_protocol_vless_websocket
    ProxyServerProtocol.Trojan -> R.string.proxy_server_protocol_trojan
    ProxyServerProtocol.Hysteria2 -> R.string.proxy_server_protocol_hysteria2
})

@Composable internal fun proxyServerFormatLabel(format: String) = stringResource(when (format) {
    ProxyServerExportFormats.URI -> R.string.proxy_server_format_uri
    ProxyServerExportFormats.SING_BOX -> R.string.proxy_server_format_sing_box
    else -> R.string.proxy_server_format_mihomo
})

/**
 * One label for the whole service, in the order the facts decide it: recovery first, then the runtime,
 * then whether it runs. [ProxyServerStatus] carries four independent booleans; a badge cannot show four.
 */
@Composable internal fun proxyServerServiceLabel(status: ProxyServerStatus) = stringResource(when {
    status.recoveryRequired -> R.string.proxy_server_state_recovery
    !status.installed -> R.string.proxy_server_state_not_installed
    status.running -> R.string.proxy_server_state_running
    else -> R.string.proxy_server_state_stopped
})

/** The wire name of [proxyServerServiceLabel]'s chip, so the chip keeps its colour across languages. */
internal fun proxyServerServiceWire(status: ProxyServerStatus) = when {
    status.recoveryRequired -> "recovery"
    !status.installed -> "notInstalled"
    status.running -> "running"
    else -> "stopped"
}

@Composable internal fun proxyServerAppliedLabel(applied: Boolean) =
    stringResource(if (applied) R.string.proxy_server_applied else R.string.proxy_server_not_applied)

@Composable internal fun proxyServerBootLabel(enabled: Boolean?) = stringResource(when (enabled) {
    true -> R.string.proxy_server_boot_enabled
    false -> R.string.proxy_server_boot_disabled
    null -> R.string.proxy_server_state_unknown
})

@Composable internal fun proxyServerStatisticsLabel(supported: Boolean) =
    stringResource(if (supported) R.string.proxy_server_statistics_supported else R.string.proxy_server_statistics_unsupported)

/**
 * A problem code grouped by the part of the surface it came from. The server owns the exact identifiers;
 * the phone only needs to tell "the draft moved" apart from "the helper is missing".
 */
@Composable internal fun proxyServerProblemLabel(code: String) = stringResource(when {
    code == "proxy_server.revision_conflict" -> R.string.proxy_server_problem_revision
    code.startsWith("proxy_server.configuration_") || code == "proxy_server.config_invalid" ||
        code == "proxy_server.config_apply_failed" -> R.string.proxy_server_problem_config
    code == "proxy_server.recovery_required" || code == "proxy_server.rollback_unavailable" ->
        R.string.proxy_server_problem_recovery
    code == "proxy_server.platform_unsupported" || code == "proxy_server.release_unsupported" ||
        code == "proxy_server.feature_unsupported" -> R.string.proxy_server_problem_platform
    code.startsWith("proxy_server.helper_") || code == "proxy_server.runtime_unavailable" ||
        code == "proxy_server.runtime_failed" || code == "proxy_server.uninstall_failed" ->
        R.string.proxy_server_problem_helper
    code.startsWith("proxy_server.idempotency_") || code == "proxy_server.operation_limit" ->
        R.string.proxy_server_problem_idempotency
    code.startsWith("proxy_server.export_") || code.startsWith("proxy_server.subscription_") ->
        R.string.proxy_server_problem_export
    code == "proxy_server.permission_denied" -> R.string.proxy_server_problem_permission
    else -> R.string.proxy_server_problem_unknown
})
