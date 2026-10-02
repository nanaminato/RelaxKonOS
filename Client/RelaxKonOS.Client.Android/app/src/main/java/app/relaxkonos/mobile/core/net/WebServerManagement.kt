package app.relaxkonos.mobile.core.net

/** Each action is gated by the current instance capabilities, including integrated instances. */
enum class WebServerAction(val route: String, val elevation: String = "nginxLifecycle") {
    Start("start"), Stop("stop"), Restart("restart"), Reload("reload"),
    EnableAcmeHttp01("enableacmehttp01", "nginxConfigurationWrite");
    val kind: String get() = if (this == EnableAcmeHttp01) "enable-acme-http01" else route
    fun supported(server: WebServer): Boolean = when (this) {
        Start -> server.canStart
        Stop -> server.canStop
        Restart -> server.canRestart
        Reload -> server.canReload
        EnableAcmeHttp01 -> server.canTestConfiguration && server.canRead
    }
}
enum class WebServerOperationState(val wire: String) {
    Queued("queued"), Running("running"), Succeeded("succeeded"), Failed("failed"), Cancelled("cancelled");
    val active: Boolean get() = this == Queued || this == Running
}
data class WebServerCandidate(val id: String, val providerId: String, val executablePath: String,
    val configurationPath: String?, val version: String?, val detectedAtMillis: Long)
data class WebServerInstallCatalog(val mainlineVersion: String?, val stableVersion: String?,
    val versions: List<String>, val problemCode: String)
data class WebServerInstallDownload(val version: String, val url: String)
data class WebServerOperation(val operationId: String, val instanceId: String, val kind: String,
    val state: WebServerOperationState, val stage: String, val problemCode: String, val snapshotId: String?,
    val startedAtMillis: Long?, val completedAtMillis: Long?)
