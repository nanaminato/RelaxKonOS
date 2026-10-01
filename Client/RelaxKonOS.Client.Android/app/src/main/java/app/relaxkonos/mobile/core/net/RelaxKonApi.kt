package app.relaxkonos.mobile.core.net

import android.os.Build
import java.io.ByteArrayOutputStream
import kotlinx.coroutines.ensureActive
import java.io.InputStream
import java.io.OutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject

/**
 * Kotlin implementation of the RelaxKonOS REST wire contract. Route names, JSON property names and
 * enum values must stay synchronized with `RelaxKonOS.Protocol`.
 *
 * The client is deliberately stateless: the current server address and access token are supplied by
 * `AuthSession`, which is also the only place that decides when a refresh happens.
 */
class RelaxKonApi(
    private val clientVersion: String,
    private val deviceName: String = defaultDeviceName(),
) : RelaxKonGateway {
    override suspend fun hostTime(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, HostSettingsRoutes.ROOT + "/time", HostSettingsWire::time)
    override suspend fun hostIdentity(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, HostSettingsRoutes.ROOT + "/identity", HostSettingsWire::identity)
    override suspend fun hostEnvironmentTarget(serverUrl: String, accessToken: String, scope: HostEnvironmentScope) = webPublishingRead(serverUrl, accessToken, HostSettingsRoutes.target(scope), HostSettingsWire::target)
    override suspend fun hostEnvironment(serverUrl: String, accessToken: String, scope: HostEnvironmentScope, reveal: Boolean) = webPublishingRead(serverUrl, accessToken, HostSettingsRoutes.environment(scope, reveal), HostSettingsWire::environment)
    override suspend fun previewHostSettings(serverUrl: String, accessToken: String, kind: HostSettingKind, expectedRevision: String, key: String,
        value: String?, scope: HostEnvironmentScope?, mutation: HostEnvironmentMutation?, confirmHighImpact: Boolean): ApiResult<HostSettingsPlan> {
        require(HostSettingsRules.revision(expectedRevision) && key.length in 1..128 && key.none { it.isISOControl() })
        val body = JsonBody().string("expectedRevision", expectedRevision).string("idempotencyKey", key)
        when (kind) {
            HostSettingKind.Time -> body.raw("change", JSONObject().put("timeZoneId", requireNotNull(value)).toString())
            HostSettingKind.Identity -> body.raw("change", JSONObject().put("hostName", requireNotNull(value)).toString())
            HostSettingKind.Environment -> {
                val m = requireNotNull(mutation)
                body.string("scope", requireNotNull(scope).query).raw("change", JSONObject().put("confirmHighImpact", confirmHighImpact)
                    .put("changes", JSONArray().put(JSONObject().put("name", m.name).put("operation", if (m.delete) "delete" else "set")
                        .put("value", m.value ?: JSONObject.NULL).put("valueKind", if (m.expand) "expandString" else "string"))).toString())
            }
        }
        return webPublishingCall("POST", serverUrl, HostSettingsRoutes.ROOT + "/${kind.path}/preview", accessToken, body, HostSettingsWire::plan)
    }
    override suspend fun applyHostSettings(serverUrl: String, accessToken: String, kind: HostSettingKind, planId: String) =
        webPublishingCall("POST", serverUrl, HostSettingsRoutes.ROOT + "/${kind.path}/apply", accessToken,
            JsonBody().string("planId", HostSettingsRules.id(planId)), HostSettingsWire::operation)
    override suspend fun hostSettingsOperation(serverUrl: String, accessToken: String, id: String) =
        webPublishingRead(serverUrl, accessToken, HostSettingsRoutes.operation(id), HostSettingsWire::operation)
    override suspend fun rollbackHostSettings(serverUrl: String, accessToken: String, id: String, expectedRevision: String): ApiResult<HostSettingsOperation> {
        require(HostSettingsRules.revision(expectedRevision))
        return webPublishingCall("POST", serverUrl, HostSettingsRoutes.operation(id) + "/rollback", accessToken,
            JsonBody().string("expectedRevision", expectedRevision), HostSettingsWire::operation)
    }

    override suspend fun smbStatus(serverUrl: String, accessToken: String): ApiResult<SmbStatus> = webPublishingRead(serverUrl, accessToken, SmbRoutes.ROOT + "/status", SmbWire::status)
    override suspend fun smbCapabilities(serverUrl: String, accessToken: String): ApiResult<SmbCapabilities> = webPublishingRead(serverUrl, accessToken, SmbRoutes.ROOT + "/capabilities", SmbWire::capabilities)
    override suspend fun smbShares(serverUrl: String, accessToken: String): ApiResult<List<SmbShare>> = webPublishingRead(serverUrl, accessToken, SmbRoutes.ROOT + "/shares", SmbWire::shares)
    override suspend fun smbUsers(serverUrl: String, accessToken: String): ApiResult<List<SmbUser>> = webPublishingRead(serverUrl, accessToken, SmbRoutes.ROOT + "/users", SmbWire::users)
    override suspend fun smbConnection(serverUrl: String, accessToken: String): ApiResult<SmbConnection> = webPublishingRead(serverUrl, accessToken, SmbRoutes.ROOT + "/connection", SmbWire::connection)
    override suspend fun smbChange(serverUrl: String, accessToken: String, change: SmbChange, password: CharArray?): ApiResult<SmbReceipt> =
        webPublishingCall(SmbRoutes.method(change.kind), serverUrl, SmbRoutes.route(change), accessToken,
            if (change.kind == SmbChangeKind.Password) JsonBody().secret("password", requireNotNull(password)) else change.share?.body(), SmbWire::receipt)
    override suspend fun firewallStatus(serverUrl: String, accessToken: String): ApiResult<FirewallStatus> = webPublishingRead(serverUrl, accessToken, FirewallRoutes.ROOT + "/status", FirewallWire::status)
    override suspend fun firewallRules(serverUrl: String, accessToken: String): ApiResult<List<FirewallRule>> = webPublishingRead(serverUrl, accessToken, FirewallRoutes.ROOT + "/rules", FirewallWire::rules)
    override suspend fun changeFirewall(serverUrl: String, accessToken: String, change: FirewallChange, password: CharArray?): ApiResult<FirewallResult> =
        webPublishingCall(FirewallRoutes.method(change.kind), serverUrl, FirewallRoutes.route(change), accessToken, change.body(password), FirewallWire::result)
    override suspend fun proxySettings(serverUrl: String, accessToken: String): ApiResult<ProxySettings> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/settings", ProxyDiagnosticsWire::settings)
    override suspend fun proxyRecovery(serverUrl: String, accessToken: String): ApiResult<ProxyRecovery> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/recovery", ProxyDiagnosticsWire::recovery)
    override suspend fun proxyTun(serverUrl: String, accessToken: String): ApiResult<ProxyRecovery> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/tun", ProxyDiagnosticsWire::recovery)
    override suspend fun proxyTraffic(serverUrl: String, accessToken: String): ApiResult<ProxyTraffic> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/traffic", ProxyDiagnosticsWire::traffic)
    override suspend fun proxyConnections(serverUrl: String, accessToken: String): ApiResult<List<ProxyConnection>> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/connections", ProxyDiagnosticsWire::connections)
    override suspend fun proxyLogs(serverUrl: String, accessToken: String): ApiResult<List<ProxyLog>> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/logs?limit=100", ProxyDiagnosticsWire::logs)
    override suspend fun proxyDns(serverUrl: String, accessToken: String): ApiResult<ProxyDns> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/dns", ProxyDiagnosticsWire::dns)
    override suspend fun proxyGeoData(serverUrl: String, accessToken: String): ApiResult<ProxyGeoData> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/geodata", ProxyDiagnosticsWire::geoData)
    override suspend fun saveProxySettings(serverUrl: String, accessToken: String, settings: ProxySettings): ApiResult<Unit> = tunnelEmpty("PUT", serverUrl, accessToken, ProxyRoutes.ROOT + "/settings", settings.body())
    override suspend fun configureProxyGeoData(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> = tunnelEmpty("PUT", serverUrl, accessToken, ProxyRoutes.ROOT + "/geodata", JsonBody().string("filePath", path))
    override suspend fun closeProxyConnection(serverUrl: String, accessToken: String, id: String): ApiResult<Unit> = tunnelEmpty("DELETE", serverUrl, accessToken, ProxyRoutes.ROOT + "/connections/" + ProxyRoutes.segment(id))
    override suspend fun proxyOverview(serverUrl: String, accessToken: String): ApiResult<ProxyOverview> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT, ProxyWire::overview)
    override suspend fun proxyProfiles(serverUrl: String, accessToken: String): ApiResult<List<ProxyProfile>> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.PROFILES, ProxyWire::profiles)
    override suspend fun proxySubscriptions(serverUrl: String, accessToken: String): ApiResult<List<ProxySubscription>> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.SUBSCRIPTIONS, ProxyWire::subscriptions)
    override suspend fun proxyGroups(serverUrl: String, accessToken: String): ApiResult<List<ProxyGroup>> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.GROUPS, ProxyWire::groups)
    override suspend fun proxyRouting(serverUrl: String, accessToken: String): ApiResult<ProxyRoutingMode> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROUTING, ProxyWire::routing)
    override suspend fun proxyDownloadOptions(serverUrl: String, accessToken: String): ApiResult<Boolean> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.SUBSCRIPTIONS + "/download-options", ProxyWire::downloadOptions)
    override suspend fun proxyOperation(serverUrl: String, accessToken: String, id: String): ApiResult<ProxyOperation> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.operation(id), ProxyWire::operation)
    override suspend fun proxyDownload(serverUrl: String, accessToken: String, version: String): ApiResult<ProxyDownload> = webPublishingRead(serverUrl, accessToken, ProxyRoutes.ROOT + "/runtime/download?version=" + ProxyRoutes.segment(version), ProxyWire::download)
    override suspend fun proxyQueue(serverUrl: String, accessToken: String, action: ProxyAction, target: String?, key: String): ApiResult<String> = deploymentMutation("POST", serverUrl, ProxyRoutes.action(action, target), accessToken, if (action == ProxyAction.EnableTun) JsonBody().string("profileId", InstallationRoutes.canonicalId(requireNotNull(target))) else JsonBody(), key, ProxyWire::accepted)
    override suspend fun saveProxyProfile(serverUrl: String, accessToken: String, id: String?, request: ProxyProfileRequest): ApiResult<ProxyProfile> = webPublishingCall(if (id == null) "POST" else "PUT", serverUrl, id?.let(ProxyRoutes::profile) ?: ProxyRoutes.PROFILES, accessToken, request.body(), ProxyWire::profile)
    override suspend fun activateProxyProfile(serverUrl: String, accessToken: String, id: String): ApiResult<ProxyProfile> = webPublishingCall("POST", serverUrl, ProxyRoutes.profile(id) + "/activate", accessToken, JsonBody(), ProxyWire::profile)
    override suspend fun deleteProxyProfile(serverUrl: String, accessToken: String, id: String): ApiResult<Unit> = tunnelEmpty("DELETE", serverUrl, accessToken, ProxyRoutes.profile(id))
    override suspend fun applyProxyConfiguration(serverUrl: String, accessToken: String, id: String, yaml: String): ApiResult<Unit> = tunnelEmpty("POST", serverUrl, accessToken, ProxyRoutes.profile(id) + "/configuration/apply", JsonBody().string("yaml", yaml))
    override suspend fun importProxySubscription(serverUrl: String, accessToken: String, request: ProxyImportRequest): ApiResult<ProxySubscription> = webPublishingCall("POST", serverUrl, ProxyRoutes.SUBSCRIPTIONS, accessToken, request.body(), ProxyWire::subscription)
    override suspend fun selectProxyNode(serverUrl: String, accessToken: String, group: String, proxy: String): ApiResult<Unit> = tunnelEmpty("PUT", serverUrl, accessToken, ProxyRoutes.selection(group), JsonBody().string("proxy", proxy))
    override suspend fun setProxyRouting(serverUrl: String, accessToken: String, mode: ProxyRoutingMode): ApiResult<Unit> = tunnelEmpty("PUT", serverUrl, accessToken, ProxyRoutes.ROUTING, JsonBody().string("mode", mode.wire))
    override suspend fun testProxyDelay(serverUrl: String, accessToken: String, group: String, proxy: String, url: String, timeout: Int): ApiResult<ProxyDelay> = webPublishingCall("POST", serverUrl, ProxyRoutes.delay(group, proxy), accessToken, JsonBody().string("url", url).raw("timeoutMilliseconds", timeout.toString()), ProxyWire::delay)
    override suspend fun createDefinitionBackup(serverUrl: String, accessToken: String, applicationId: String, idempotencyKey: String): ApiResult<BackupManifest> =
        backupMutation(serverUrl, BackupRecoveryRoutes.definitionBackup(applicationId), accessToken, idempotencyKey, BackupRecoveryWire::manifest)
    override suspend fun definitionBackupRequest(serverUrl: String, accessToken: String, applicationId: String, idempotencyKey: String): ApiResult<BackupManifest?> =
        when (val result = execute("GET", serverUrl, BackupRecoveryRoutes.definitionBackupRequest(applicationId), accessToken, null,
            mapOf("Idempotency-Key" to idempotencyKey))) {
            is ApiResult.Success -> runCatching { BackupRecoveryWire.manifest(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed backup recovery response.") })
            is ApiResult.Problem -> if (result.status == 404) ApiResult.Success(null) else result
            is ApiResult.Transport -> result
        }
    override suspend fun backupManifests(serverUrl: String, accessToken: String, applicationId: String): ApiResult<List<BackupManifest>> = backupCall("GET", serverUrl, BackupRecoveryRoutes.backups(applicationId), accessToken, BackupRecoveryWire::manifests)
    override suspend fun backupManifest(serverUrl: String, accessToken: String, backupId: String): ApiResult<BackupManifest> = backupCall("GET", serverUrl, BackupRecoveryRoutes.backup(backupId), accessToken, BackupRecoveryWire::manifest)
    override suspend fun backupPreflight(serverUrl: String, accessToken: String, backupId: String): ApiResult<BackupPreflight> = backupCall("GET", serverUrl, BackupRecoveryRoutes.preflight(backupId), accessToken, BackupRecoveryWire::preflight)
    private suspend fun <T> backupCall(method: String, serverUrl: String, route: String, accessToken: String, parse: (String) -> T): ApiResult<T> = when (val result = execute(method, serverUrl, route, accessToken, null)) {
        is ApiResult.Success -> runCatching { parse(result.value) }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed backup recovery response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }
    private suspend fun <T> backupMutation(serverUrl: String, route: String, accessToken: String, idempotencyKey: String,
        parse: (String) -> T): ApiResult<T> = when (val result = execute("POST", serverUrl, route, accessToken, JsonBody(),
            mapOf("Idempotency-Key" to idempotencyKey))) {
        is ApiResult.Success -> runCatching { parse(result.value) }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed backup recovery response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }
    override suspend fun alerts(serverUrl: String, accessToken: String, cursor: String?, query: AlertQuery): ApiResult<OperationalAlertPage> =
        eventAlertCall("GET", serverUrl, EventAlertRoutes.page(cursor, query), accessToken, null, EventAlertWire::page)

    override suspend fun operationalEvents(serverUrl: String, accessToken: String, cursor: String?, query: EventQuery): ApiResult<OperationalEventPage> =
        eventAlertCall("GET", serverUrl, EventAlertRoutes.events(cursor, query), accessToken, null, EventAlertWire::events)
    override suspend fun eventAlertSummary(serverUrl: String, accessToken: String): ApiResult<EventAlertSummary> =
        eventAlertCall("GET", serverUrl, EventAlertRoutes.SUMMARY, accessToken, null, EventAlertWire::summary)
    override suspend fun mutateAlert(serverUrl: String, accessToken: String, id: String, action: AlertMutation, reason: String?, expiresAt: String?): ApiResult<OperationalAlert> {
        val route = when (action) {
            AlertMutation.Acknowledge -> EventAlertRoutes.acknowledgement(id)
            AlertMutation.Resolve -> EventAlertRoutes.resolve(id)
            AlertMutation.Suppress, AlertMutation.RemoveSuppression -> EventAlertRoutes.suppression(id)
        }
        val body = if (action == AlertMutation.RemoveSuppression) null else JsonBody().apply {
            if (action == AlertMutation.Acknowledge) raw("note", reason?.let(JSONObject::quote) ?: "null")
            else string("reason", reason)
            if (action == AlertMutation.Suppress) string("expiresAt", expiresAt)
        }
        return eventAlertCall(if (action == AlertMutation.RemoveSuppression) "DELETE" else "POST", serverUrl, route, accessToken, body, EventAlertWire::alert)
    }

    override suspend fun alertDetail(serverUrl: String, accessToken: String, id: String): ApiResult<OperationalAlertDetail> =
        eventAlertCall("GET", serverUrl, EventAlertRoutes.alert(id), accessToken, null, EventAlertWire::detail)



    private suspend fun <T> eventAlertCall(method: String, serverUrl: String, route: String, accessToken: String,
        body: JsonBody?, parse: (String) -> T): ApiResult<T> = when (val result = execute(method, serverUrl, route, accessToken, body)) {
        is ApiResult.Success -> runCatching { parse(result.value) }
            .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed operational alert response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }

    override suspend fun scriptTasks(serverUrl: String, accessToken: String): ApiResult<ScriptTasksResult> =
        guardianCall("GET", serverUrl, ScriptRoutes.TASKS, accessToken, null, ScriptWire::tasks)
    override suspend fun scriptTask(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> =
        guardianCall("GET", serverUrl, ScriptRoutes.task(id), accessToken, null, ScriptWire::taskResult)
    override suspend fun scriptSubmit(serverUrl: String, accessToken: String, request: ScriptRequest, key: String): ApiResult<ScriptTaskResult> {
        val body = JsonBody().string("executablePath", request.executablePath)
            .raw("arguments", JSONArray(request.arguments).toString())
            .string("workingDirectory", request.workingDirectory)
            .raw("environment", JSONObject(request.environment).toString())
            .int("timeoutSeconds", request.timeoutSeconds).string("runAs", request.runAs)
        request.approval?.let { body.objectField("runAsApproval", JsonBody().string("username", it.username).secret("password", it.password)) }
        return when (val result = execute("POST", serverUrl, ScriptRoutes.TASKS, accessToken, body, mapOf("Idempotency-Key" to key))) {
            is ApiResult.Success -> runCatching { ScriptWire.taskResult(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed script response.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }
    override suspend fun scriptCancel(serverUrl: String, accessToken: String, id: String): ApiResult<ScriptTaskResult> =
        guardianCall("POST", serverUrl, ScriptRoutes.cancel(id), accessToken, JsonBody(), ScriptWire::taskResult)

    override suspend fun guardianStatus(serverUrl: String, accessToken: String): ApiResult<GuardianStatus> =
        guardianCall("GET", serverUrl, GuardianRoutes.STATUS, accessToken, null, GuardianWire::status)
    override suspend fun guardianWorkloads(serverUrl: String, accessToken: String): ApiResult<List<GuardianWorkload>> =
        guardianCall("GET", serverUrl, GuardianRoutes.WORKLOADS, accessToken, null, GuardianWire::workloads)
    override suspend fun guardianDefinition(serverUrl: String, accessToken: String, id: String): ApiResult<GuardianDefinitionResult> =
        guardianCall("GET", serverUrl, GuardianRoutes.workload(id), accessToken, null, GuardianWire::definition)
    override suspend fun guardianLogs(serverUrl: String, accessToken: String, id: String): ApiResult<List<GuardianLog>> =
        guardianCall("GET", serverUrl, GuardianRoutes.logs(id), accessToken, null, GuardianWire::logs)
    override suspend fun guardianSave(serverUrl: String, accessToken: String, definition: GuardianDefinition, approval: GuardianApproval?): ApiResult<GuardianDefinitionResult> {
        val body = JsonBody().raw("definition", GuardianWire.definitionJson(definition))
        if (approval != null) body.objectField("runAsApproval", JsonBody().string("username", approval.username).secret("password", approval.password))
        return guardianCall("POST", serverUrl, GuardianRoutes.WORKLOADS, accessToken, body, GuardianWire::definition)
    }
    override suspend fun guardianAction(serverUrl: String, accessToken: String, id: String, action: String): ApiResult<GuardianOperation> =
        guardianCall("POST", serverUrl, GuardianRoutes.action(id, action), accessToken, JsonBody(), GuardianWire::operation)
    override suspend fun guardianDelete(serverUrl: String, accessToken: String, id: String): ApiResult<GuardianOperation> =
        guardianCall("DELETE", serverUrl, GuardianRoutes.workload(id), accessToken, null, GuardianWire::operation)

    private suspend fun <T> guardianCall(method: String, serverUrl: String, route: String, accessToken: String,
        body: JsonBody?, parse: (String) -> T): ApiResult<T> = when (val result = execute(method, serverUrl, route, accessToken, body)) {
        is ApiResult.Success -> runCatching { parse(result.value) }
            .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed Guardian response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }

    override suspend fun textFile(serverUrl: String, accessToken: String, path: String): ApiResult<RemoteTextFile> =
        gitCall("GET", serverUrl, TextEditorRoutes.file(path), accessToken, null, TextEditorWire::file)

    override suspend fun saveTextFile(serverUrl: String, accessToken: String, file: RemoteTextFile, content: String): ApiResult<RemoteTextFile> =
        gitCall("PUT", serverUrl, TextEditorRoutes.file(file.path), accessToken,
            JsonBody().string("content", content).string("expectedVersion", file.version)
                .string("encoding", file.encoding).bool("bom", file.bom), TextEditorWire::file)

    override suspend fun createTextFile(serverUrl: String, accessToken: String, path: String, content: String, encoding: String, bom: Boolean): ApiResult<RemoteTextFile> =
        gitCall("POST", serverUrl, TextEditorRoutes.file(path), accessToken,
            JsonBody().string("content", content).string("encoding", encoding).bool("bom", bom), TextEditorWire::file)

    override suspend fun gitEngine(serverUrl: String, accessToken: String): ApiResult<GitEngine> =
        gitCall("GET", serverUrl, GitWorkspaceRoutes.ENGINE, accessToken, null, GitWorkspaceWire::engine)
    override suspend fun gitDiff(serverUrl: String, accessToken: String, id: String, path: String, staged: Boolean, reference: String?): ApiResult<GitDiff> =
        gitCall("GET", serverUrl, GitWorkspaceRoutes.diff(id, path, staged, reference), accessToken, null, GitWorkspaceWire::diff)
    override suspend fun gitLog(serverUrl: String, accessToken: String, id: String, skip: Int, search: String): ApiResult<List<GitCommit>> =
        gitCall("GET", serverUrl, GitWorkspaceRoutes.log(id, skip, search), accessToken, null, GitWorkspaceWire::log)
    override suspend fun gitCommitDetail(serverUrl: String, accessToken: String, id: String, sha: String): ApiResult<GitCommitDetail> =
        gitCall("GET", serverUrl, GitWorkspaceRoutes.commit(id, sha), accessToken, null, GitWorkspaceWire::detail)
    override suspend fun gitConflicts(serverUrl: String, accessToken: String, id: String): ApiResult<GitConflictState> =
        gitCall("GET", serverUrl, GitWorkspaceRoutes.conflicts(id), accessToken, null, GitWorkspaceWire::conflicts)
    override suspend fun gitConflict(serverUrl: String, accessToken: String, id: String, path: String): ApiResult<GitConflictFile> =
        gitCall("GET", serverUrl, GitWorkspaceRoutes.conflict(id, path), accessToken, null, GitWorkspaceWire::conflict)
    override suspend fun gitMutation(serverUrl: String, accessToken: String, id: String, change: GitMutation): ApiResult<GitOperation> {
        GitWorkspacePolicy.validate(change)
        val body = when (change.action) {
            GitAction.Checkout -> JsonBody().string("branch", requireNotNull(change.branch)).bool("createIfMissing", false)
            GitAction.CreateBranch -> JsonBody().string("name", requireNotNull(change.branch)).bool("checkout", true).bool("track", false).bool("resetExisting", false)
            GitAction.Stage, GitAction.Unstage -> JsonBody().raw("paths", JSONArray(change.paths).toString())
            GitAction.Commit -> JsonBody().string("message", requireNotNull(change.message)).raw("paths", "[]").bool("amend", false)
            GitAction.Pull -> JsonBody().string("strategy", change.strategy)
            GitAction.Push -> JsonBody().bool("saveCredentials", false)
            GitAction.Resolve -> JsonBody().string("path", requireNotNull(change.conflict).path).string("revision", change.conflict.revision)
                .string("choice", requireNotNull(change.choice)).apply { change.content?.let { string("content", it) } }
            GitAction.Continue, GitAction.Abort -> JsonBody().string("operation", requireNotNull(change.operation)).string("action", if (change.action == GitAction.Continue) "continue" else "abort")
            GitAction.Fetch -> JsonBody()
            GitAction.DeleteBranch -> null
        }
        return gitCall(if (change.action == GitAction.DeleteBranch) "DELETE" else "POST", serverUrl,
            GitWorkspaceRoutes.mutation(id, change), accessToken, body, GitWire::operation)
    }

    override suspend fun gitRepositories(serverUrl: String, accessToken: String): ApiResult<List<GitRepository>> =
        gitCall("GET", serverUrl, GitRoutes.REPOSITORIES, accessToken, null, GitWire::repositories)

    override suspend fun gitRegisterRepository(serverUrl: String, accessToken: String, name: String, path: String): ApiResult<GitRepository> =
        gitCall("POST", serverUrl, GitRoutes.REPOSITORIES, accessToken,
            JsonBody().string("name", name.trim()).string("path", path.trim()), GitWire::repository)

    override suspend fun gitBranches(serverUrl: String, accessToken: String, id: String): ApiResult<List<GitBranch>> =
        gitCall("GET", serverUrl, GitRoutes.branches(id), accessToken, null, GitWire::branches)

    override suspend fun gitStatus(serverUrl: String, accessToken: String, id: String): ApiResult<GitStatus> =
        gitCall("GET", serverUrl, GitRoutes.status(id), accessToken, null, GitWire::status)

    override suspend fun gitTextFile(serverUrl: String, accessToken: String, id: String, path: String): ApiResult<RemoteTextFile> =
        gitCall("GET", serverUrl, GitRoutes.textFile(id, path), accessToken, null, GitWire::textFile)

    override suspend fun gitSaveTextFile(serverUrl: String, accessToken: String, id: String, file: RemoteTextFile, content: String): ApiResult<RemoteTextFile> =
        gitCall("PUT", serverUrl, GitRoutes.textFile(id, file.path), accessToken,
            JsonBody().string("content", content).string("expectedVersion", file.version)
                .string("encoding", file.encoding).bool("bom", file.bom), GitWire::textFile)

    override suspend fun gitBuildCredentials(serverUrl: String, accessToken: String): ApiResult<List<GitBuildCredential>> =
        gitCall("GET", serverUrl, GitBuildRoutes.CREDENTIALS, accessToken, null, GitBuildWire::credentials)

    override suspend fun gitBuildSetCredential(serverUrl: String, accessToken: String, name: String, token: String): ApiResult<GitBuildCredential> =
        gitCall("POST", serverUrl, GitBuildRoutes.CREDENTIALS, accessToken,
            JsonBody().string("name", name.trim()).string("token", token), GitBuildWire::credential)

    override suspend fun gitBuildResolve(serverUrl: String, accessToken: String, url: String, reference: String, credentialId: String?): ApiResult<GitBuildResolved> =
        gitCall("POST", serverUrl, GitBuildRoutes.RESOLVE, accessToken,
            JsonBody().string("repositoryUrl", url.trim()).string("reference", reference.trim())
                .string("credentialId", credentialId), GitBuildWire::resolved)

    override suspend fun gitBuildRefs(serverUrl: String, accessToken: String, url: String, credentialId: String?): ApiResult<List<GitBuildRef>> =
        gitCall("POST", serverUrl, GitBuildRoutes.REFS, accessToken,
            JsonBody().string("repositoryUrl", url.trim()).string("reference", "main")
                .string("credentialId", credentialId), GitBuildWire::refs)

    override suspend fun gitBuilds(serverUrl: String, accessToken: String): ApiResult<List<GitBuildOperation>> =
        gitCall("GET", serverUrl, GitBuildRoutes.ROOT, accessToken, null, GitBuildWire::operations)

    override suspend fun gitBuildStart(serverUrl: String, accessToken: String, request: GitBuildRequest, key: String): ApiResult<GitBuildOperation> =
        when (val result = execute("POST", serverUrl, GitBuildRoutes.ROOT, accessToken,
            JsonBody().string("repositoryUrl", request.repositoryUrl).string("reference", request.reference)
                .string("commitSha", request.commitSha).string("contextDirectory", request.contextDirectory)
                .string("dockerfile", request.dockerfile).string("credentialId", request.credentialId),
            mapOf("Idempotency-Key" to key))) {
            is ApiResult.Success -> runCatching { GitBuildWire.operation(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed Git build response.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    override suspend fun gitBuildGet(serverUrl: String, accessToken: String, id: String): ApiResult<GitBuildOperation> =
        gitCall("GET", serverUrl, GitBuildRoutes.operation(id), accessToken, null, GitBuildWire::operation)

    override suspend fun gitBuildCancel(serverUrl: String, accessToken: String, id: String): ApiResult<GitBuildOperation> =
        gitCall("POST", serverUrl, GitBuildRoutes.cancel(id), accessToken, JsonBody(), GitBuildWire::operation)

    override suspend fun deployGitBuild(serverUrl: String, accessToken: String, applicationId: String,
        build: GitBuildOperation, expectedUpdatedAt: String, key: String): ApiResult<DeploymentOperation> {
        val source = JSONObject().put("imageReference", build.imageReference).put("gitBuildId", build.id).toString()
        return deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.deploy(applicationId), accessToken,
            JsonBody().raw("source", source).string("expectedUpdatedAt", expectedUpdatedAt).bool("confirmed", true), key, ApplicationDeploymentWire::acceptedOperation)
    }

    private suspend fun <T> gitCall(method: String, serverUrl: String, route: String, accessToken: String,
        body: JsonBody?, parse: (String) -> T): ApiResult<T> = when (val result = execute(method, serverUrl, route, accessToken, body)) {
        is ApiResult.Success -> runCatching { parse(result.value) }
            .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed Git response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }
    override suspend fun startInstallation(serverUrl: String, accessToken: String, kind: InstallationKind,
        request: InstallationRequest, idempotencyKey: String): ApiResult<InstallationOperation> =
        installationCall("POST", serverUrl, InstallationRoutes.start(request.service, kind), accessToken,
            InstallationWire.request(request, kind), idempotencyKey, InstallationWire::operation)

    override suspend fun installation(serverUrl: String, accessToken: String, operationId: String): ApiResult<InstallationOperation> =
        installationCall("GET", serverUrl, InstallationRoutes.operation(operationId), accessToken, null, null, InstallationWire::operation)

    override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService): ApiResult<InstallationOperation?> =
        when (val result = installationCall("GET", serverUrl, InstallationRoutes.active(service), accessToken, null, null, InstallationWire::operation)) {
            is ApiResult.Problem -> if (result.status == 404 && result.code.isBlank()) ApiResult.Success(null) else result
            else -> result
        }

    override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String,
        idempotencyKey: String): ApiResult<InstallationOperation> =
        installationCall("POST", serverUrl, InstallationRoutes.cancel(operationId), accessToken, null, idempotencyKey, InstallationWire::operation)

    override suspend fun installationFileReference(serverUrl: String, accessToken: String, service: InstallationService,
        path: String): ApiResult<InstallationFileReference> = installationCall("POST", serverUrl,
        InstallationRoutes.fileReference(service), accessToken, JsonBody().string("path", path), null, InstallationWire::fileReference)

    private suspend fun <T> installationCall(method: String, serverUrl: String, route: String, accessToken: String,
        body: JsonBody?, key: String?, parse: (String) -> T): ApiResult<T> = when (val result = execute(method,
        serverUrl, route, accessToken, body, key?.let { mapOf("Idempotency-Key" to it) }.orEmpty())) {
        is ApiResult.Success -> runCatching { parse(result.value) }
            .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed installation response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }

    override suspend fun uploadInstallationPackage(serverUrl: String, accessToken: String, service: InstallationService,
        fileName: String, length: Long?, open: () -> InputStream,
        onProgress: ((Long) -> Unit)?): ApiResult<InstallationFileReference> = withContext(Dispatchers.IO) {
        val maximum = 128L * 1024 * 1024
        if (length != null && (length <= 0 || length > maximum))
            return@withContext ApiResult.Problem(400, InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE, null)
        var connection: HttpURLConnection? = null
        try {
            val boundary = "RelaxKonOS-installation-${java.util.UUID.randomUUID()}"
            val header = buildMultipartHeader(boundary, fileName.substringAfterLast('/').substringAfterLast('\\'), "package")
            val footer = "\r\n--$boundary--\r\n".toByteArray(Charsets.UTF_8)
            connection = openConnection(serverUrl, InstallationRoutes.packageUpload(service), "POST", accessToken, CHUNK_READ_TIMEOUT_MILLIS)
            connection.instanceFollowRedirects = false
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
            if (length != null) connection.setFixedLengthStreamingMode(header.size.toLong() + length + footer.size)
            else connection.setChunkedStreamingMode(BUFFER_SIZE)
            connection.outputStream.use { output ->
                output.write(header)
                open().use { input ->
                    val buffer = ByteArray(BUFFER_SIZE)
                    var written = 0L
                    while (true) {
                        kotlinx.coroutines.currentCoroutineContext().ensureActive()
                        val count = input.read(buffer)
                        if (count < 0) break
                        written += count
                        if (written > maximum || (length != null && written > length))
                            return@withContext ApiResult.Problem(400, InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE, null)
                        output.write(buffer, 0, count)
                        onProgress?.invoke(written)
                    }
                    if (written == 0L || (length != null && written != length))
                        return@withContext ApiResult.Problem(400, InstallationProblemCodes.FILE_REFERENCE_UNAVAILABLE, null)
                }
                output.write(footer)
            }
            val code = connection.responseCode
            if (code !in 200..299) return@withContext readProblem(connection, code)
            runCatching { InstallationWire.fileReference(connection.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed installation package response.") })
        } catch (error: CancellationException) {
            throw error
        } catch (_: Exception) {
            ApiResult.Transport("Installation package transfer unavailable.")
        } finally {
            connection?.disconnect()
        }
    }

    override suspend fun managedFrps(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, ManagedFrpsRoutes.ROOT, ManagedFrpsWire::configuration)
    override suspend fun managedFrpsEditing(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, ManagedFrpsRoutes.EDITOR, ManagedFrpsWire::editing)
    override suspend fun saveManagedFrps(serverUrl: String, accessToken: String, request: ManagedFrpsRequest) = webPublishingCall("PUT", serverUrl, ManagedFrpsRoutes.ROOT, accessToken, request.body(), ManagedFrpsWire::configuration)
    override suspend fun startManagedFrps(serverUrl: String, accessToken: String) = webPublishingCall("POST", serverUrl, ManagedFrpsRoutes.START, accessToken, JsonBody(), TunnelWire::result)
    override suspend fun stopManagedFrps(serverUrl: String, accessToken: String) = webPublishingCall("POST", serverUrl, ManagedFrpsRoutes.STOP, accessToken, JsonBody(), TunnelWire::result)
    override suspend fun managedFrpsLogs(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, ManagedFrpsRoutes.LOGS, TunnelWire::logs)
    override suspend fun managedFrpsAudit(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, ManagedFrpsRoutes.AUDIT, ManagedFrpsWire::audit)
    override suspend fun tunnelProfiles(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, TunnelRoutes.PROFILES, TunnelWire::profiles)
    override suspend fun tunnelDefinitions(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, TunnelRoutes.ROOT, TunnelWire::definitions)
    override suspend fun tunnelRuntime(serverUrl: String, accessToken: String) = webPublishingRead(serverUrl, accessToken, TunnelRoutes.RUNTIME, TunnelWire::runtime)
    override suspend fun tunnelRuntimeDownload(serverUrl: String, accessToken: String, version: String) = webPublishingRead(serverUrl, accessToken, TunnelRoutes.download(version), TunnelWire::download)
    override suspend fun detectTunnelRuntime(serverUrl: String, accessToken: String, path: String) = webPublishingCall("POST", serverUrl, TunnelRoutes.DETECT, accessToken, JsonBody().string("executablePath", path), TunnelWire::runtime)
    override suspend fun saveTunnelProfile(serverUrl: String, accessToken: String, id: String?, request: TunnelProfileRequest) = webPublishingCall(if (id == null) "POST" else "PUT", serverUrl, id?.let(TunnelRoutes::profile) ?: TunnelRoutes.PROFILES, accessToken, request.body(), TunnelWire::profile)
    override suspend fun deleteTunnelProfile(serverUrl: String, accessToken: String, id: String) = tunnelEmpty("DELETE", serverUrl, accessToken, TunnelRoutes.profile(id))
    override suspend fun setTunnelToken(serverUrl: String, accessToken: String, id: String, secret: String) = tunnelEmpty("PUT", serverUrl, accessToken, TunnelRoutes.token(id), JsonBody().string("token", secret))
    override suspend fun saveTunnelDefinition(serverUrl: String, accessToken: String, id: String?, request: TunnelDefinitionRequest) = webPublishingCall(if (id == null) "POST" else "PUT", serverUrl, id?.let(TunnelRoutes::definition) ?: TunnelRoutes.ROOT, accessToken, request.body(), TunnelWire::definition)
    override suspend fun deleteTunnelDefinition(serverUrl: String, accessToken: String, id: String) = tunnelEmpty("DELETE", serverUrl, accessToken, TunnelRoutes.definition(id))
    override suspend fun applyTunnelProfile(serverUrl: String, accessToken: String, id: String) = webPublishingCall("POST", serverUrl, TunnelRoutes.apply(id), accessToken, JsonBody(), TunnelWire::result)
    override suspend fun stopTunnelProfile(serverUrl: String, accessToken: String, id: String) = webPublishingCall("POST", serverUrl, TunnelRoutes.stop(id), accessToken, JsonBody(), TunnelWire::result)
    override suspend fun tunnelLogs(serverUrl: String, accessToken: String, id: String) = webPublishingRead(serverUrl, accessToken, TunnelRoutes.logs(id), TunnelWire::logs)
    private suspend fun tunnelEmpty(method: String, serverUrl: String, accessToken: String, route: String, body: JsonBody? = null): ApiResult<Unit> =
        when (val result = execute(method, serverUrl, route, accessToken, body)) {
            is ApiResult.Success -> if (result.value.isBlank()) ApiResult.Success(Unit) else ApiResult.Transport(null)
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    override suspend fun certificate(serverUrl: String, accessToken: String, id: String): ApiResult<ManagedCertificate> =
        webPublishingCall("GET", serverUrl, CertificateRoutes.certificate(id), accessToken, null, CertificateWire::certificate)
    override suspend fun kestrelCertificateDeployment(serverUrl: String, accessToken: String, id: String): ApiResult<KestrelCertificateDeployment> =
        webPublishingCall("GET", serverUrl, CertificateRoutes.kestrel(id), accessToken, null, CertificateWire::kestrel)
    override suspend fun certificatePreflight(serverUrl: String, accessToken: String, domains: List<String>, challenge: CertificateChallenge): ApiResult<CertificatePreflight> =
        webPublishingCall("POST", serverUrl, CertificateRoutes.PREFLIGHT, accessToken,
            JsonBody().raw("domains", org.json.JSONArray(domains).toString()).string("challengeType", challenge.wire), CertificateWire::preflight)
    override suspend fun certificateMutation(serverUrl: String, accessToken: String, action: CertificateAction, id: String?, body: JsonBody,
        idempotencyKey: String): ApiResult<CertificateOperation> = deploymentMutation(if (action == CertificateAction.Delete) "DELETE" else "POST",
            serverUrl, CertificateRoutes.mutation(action, id), accessToken, body, idempotencyKey, CertificateWire::operation)
    override suspend fun certificateOperation(serverUrl: String, accessToken: String, id: String): ApiResult<CertificateOperation> =
        webPublishingCall("GET", serverUrl, CertificateRoutes.operation(id), accessToken, null, CertificateWire::operation)
    override suspend fun cancelCertificateOperation(serverUrl: String, accessToken: String, id: String): ApiResult<CertificateOperation> =
        webPublishingCall("POST", serverUrl, CertificateRoutes.cancel(id), accessToken, JsonBody(), CertificateWire::operation)

    override suspend fun saveWebServerSite(serverUrl: String, accessToken: String, instanceId: String,
        request: WebServerSiteRequest): ApiResult<WebServerSite> = webPublishingCall("POST", serverUrl,
        WebPublishingRoutes.sites(instanceId), accessToken, request.body(), WebPublishingWire::site)
    override suspend fun deleteWebServerSite(serverUrl: String, accessToken: String, instanceId: String, siteId: String,
        expectedUpdatedAt: String): ApiResult<Unit> = when (val result = execute("DELETE", serverUrl, WebPublishingRoutes.site(instanceId, siteId),
        accessToken, JsonBody().string("expectedUpdatedAt", expectedUpdatedAt))) {
            is ApiResult.Success -> if (result.value.isBlank()) ApiResult.Success(Unit) else ApiResult.Transport(null)
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    override suspend fun discoverWebServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> =
        webPublishingCall("POST", serverUrl, WebPublishingRoutes.discover(), accessToken, JsonBody(), WebPublishingWire::servers)
    override suspend fun webServerCandidates(serverUrl: String, accessToken: String): ApiResult<List<WebServerCandidate>> =
        webPublishingRead(serverUrl, accessToken, WebPublishingRoutes.candidates(), WebPublishingWire::candidates)
    override suspend fun webServerInstallCatalog(serverUrl: String, accessToken: String): ApiResult<WebServerInstallCatalog> =
        webPublishingRead(serverUrl, accessToken, WebPublishingRoutes.catalog(), WebPublishingWire::catalog)
    override suspend fun integrateWebServer(serverUrl: String, accessToken: String, candidateId: String, confirmed: Boolean,
        idempotencyKey: String): ApiResult<WebServerOperation> = webPublishingMutation(serverUrl, accessToken,
        WebPublishingRoutes.integrate(candidateId), JsonBody().bool("confirmed", confirmed), idempotencyKey, WebPublishingWire::operation)
    override suspend fun webServerLifecycle(serverUrl: String, accessToken: String, instanceId: String, action: WebServerAction,
        idempotencyKey: String): ApiResult<WebServerOperation> = webPublishingMutation(serverUrl, accessToken,
        WebPublishingRoutes.lifecycle(instanceId, action), JsonBody(), idempotencyKey, WebPublishingWire::operation)
    override suspend fun webServerOperation(serverUrl: String, accessToken: String, operationId: String): ApiResult<WebServerOperation> =
        webPublishingRead(serverUrl, accessToken, WebPublishingRoutes.operation(operationId), WebPublishingWire::operation)
    override suspend fun cancelWebServerOperation(serverUrl: String, accessToken: String, operationId: String,
        idempotencyKey: String): ApiResult<WebServerOperation> = webPublishingMutation(serverUrl, accessToken,
        WebPublishingRoutes.cancel(operationId), JsonBody(), idempotencyKey, WebPublishingWire::operation)

    override suspend fun webServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> =
        webPublishingRead(serverUrl, accessToken, WebPublishingRoutes.servers(), WebPublishingWire::servers)

    override suspend fun webServerStatus(serverUrl: String, accessToken: String, instanceId: String): ApiResult<WebServerStatus> =
        webPublishingRead(serverUrl, accessToken, WebPublishingRoutes.status(instanceId), WebPublishingWire::status)

    override suspend fun webServerConfigTest(serverUrl: String, accessToken: String, instanceId: String): ApiResult<WebServerConfigTest> =
        webPublishingCall("POST", serverUrl, WebPublishingRoutes.testConfiguration(instanceId), accessToken, JsonBody(), WebPublishingWire::configTest)

    override suspend fun webServerSites(serverUrl: String, accessToken: String, instanceId: String): ApiResult<List<WebServerSite>> =
        webPublishingRead(serverUrl, accessToken, WebPublishingRoutes.sites(instanceId), WebPublishingWire::sites)

    override suspend fun certificates(serverUrl: String, accessToken: String): ApiResult<List<ManagedCertificate>> =
        webPublishingRead(serverUrl, accessToken, CertificateRoutes.ROOT, CertificateWire::list)

    override suspend fun publishWebsite(
        serverUrl: String, accessToken: String, request: WebsitePublishRequest, idempotencyKey: String,
    ): ApiResult<WebsitePublicationOperation> = webPublishingMutation(
        serverUrl, accessToken, WebPublishingRoutes.publish(),
        JsonBody().string("applicationId", request.applicationId).string("webServerId", request.webServerId)
            .string("domain", request.domain.trim()).string("certificateId", request.certificateId)
            .string("contactEmail", request.contactEmail?.trim()?.takeIf(String::isNotEmpty))
            .bool("acceptedTerms", request.acceptedTerms)
            .bool("publicReachabilityConfirmed", request.publicReachabilityConfirmed)
            .bool("confirmed", request.confirmed),
        idempotencyKey, WebPublishingWire::publication,
    )

    override suspend fun websitePublicationHistory(serverUrl: String, accessToken: String, applicationId: String): ApiResult<List<WebsitePublicationOperation>> =
        webPublishingRead(serverUrl, accessToken, WebPublishingRoutes.publicationHistory(applicationId), WebPublishingWire::publicationHistory)

    override suspend fun websitePublication(serverUrl: String, accessToken: String, operationId: String): ApiResult<WebsitePublicationOperation> =
        webPublishingRead(serverUrl, accessToken, WebPublishingRoutes.publication(operationId), WebPublishingWire::publication)

    override suspend fun outboundProxyStatus(serverUrl: String, accessToken: String): ApiResult<OutboundProxyStatus> =
        outboundProxyCall("GET", serverUrl, accessToken, null)

    override suspend fun saveOutboundProxy(serverUrl: String, accessToken: String, settings: OutboundProxySettings, confirmed: Boolean): ApiResult<OutboundProxyStatus> =
        outboundProxyCall("PUT", serverUrl, accessToken, OutboundProxyWire.request(settings, confirmed))

    override suspend fun clearOutboundProxy(serverUrl: String, accessToken: String): ApiResult<OutboundProxyStatus> =
        outboundProxyCall("DELETE", serverUrl, accessToken, null)

    private suspend fun outboundProxyCall(method: String, serverUrl: String, accessToken: String, body: JsonBody?): ApiResult<OutboundProxyStatus> =
        when (val result = execute(method, serverUrl, OutboundProxyWire.ROUTE, accessToken, body)) {
            is ApiResult.Success -> runCatching { OutboundProxyWire.status(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport(null) })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    override suspend fun dockerContainerDetails(serverUrl: String, accessToken: String, id: String): ApiResult<DockerContainerDetails> =
        webPublishingRead(serverUrl, accessToken, DockerResourceRoutes.resource(DockerResourceKind.Containers, id), DockerResourceWire::container)
    override suspend fun dockerContainerStats(serverUrl: String, accessToken: String, id: String): ApiResult<DockerContainerStats> =
        webPublishingRead(serverUrl, accessToken, DockerResourceRoutes.stats(id), DockerResourceWire::stats)
    override suspend fun dockerNetworkDetails(serverUrl: String, accessToken: String, id: String): ApiResult<DockerNetworkDetails> =
        webPublishingRead(serverUrl, accessToken, DockerResourceRoutes.resource(DockerResourceKind.Networks, id), DockerResourceWire::network)
    override suspend fun dockerResourceChange(serverUrl: String, accessToken: String, change: DockerResourceChange): ApiResult<DockerOperation> =
        webPublishingCall(DockerResourceRoutes.method(change.action), serverUrl, DockerResourceRoutes.route(change), accessToken, DockerResourceRoutes.body(change), DockerWire::operation)
    override suspend fun dockerEngineAction(serverUrl: String, accessToken: String, action: DockerEngineAction, confirmed: Boolean): ApiResult<DockerEngineResult> =
        webPublishingCall("POST", serverUrl, DockerControlRoutes.engine(action), accessToken, JsonBody().bool("confirmed", confirmed), DockerControlWire::engine)
    override suspend fun dockerMirrors(serverUrl: String, accessToken: String): ApiResult<List<DockerImageMirror>> =
        webPublishingRead(serverUrl, accessToken, DockerControlRoutes.MIRRORS, DockerControlWire::mirrors)
    override suspend fun dockerCreateMirror(serverUrl: String, accessToken: String, request: DockerMirrorRequest): ApiResult<DockerImageMirror> =
        webPublishingCall("POST", serverUrl, DockerControlRoutes.MIRRORS, accessToken, request.body(), DockerControlWire::mirror)
    override suspend fun dockerUpdateMirror(serverUrl: String, accessToken: String, id: String, request: DockerMirrorRequest): ApiResult<DockerImageMirror> =
        webPublishingCall("PUT", serverUrl, DockerControlRoutes.mirror(id), accessToken, request.body(), DockerControlWire::mirror)
    override suspend fun dockerDeleteMirror(serverUrl: String, accessToken: String, id: String): ApiResult<Unit> =
        tunnelEmpty("DELETE", serverUrl, accessToken, DockerControlRoutes.mirror(id))
    override suspend fun dockerSelectMirror(serverUrl: String, accessToken: String, id: String?): ApiResult<Unit> =
        tunnelEmpty("PUT", serverUrl, accessToken, DockerControlRoutes.MIRRORS + "/selection", JsonBody().apply {
            if (id == null) raw("mirrorId", "null") else string("mirrorId", InstallationRoutes.canonicalId(id))
        })
    override suspend fun dockerStatus(serverUrl: String, accessToken: String): ApiResult<DockerStatus> =
        dockerRead(serverUrl, accessToken, DockerRoutes.STATUS, DockerWire::status)

    override suspend fun dockerContainers(serverUrl: String, accessToken: String): ApiResult<List<DockerContainer>> =
        dockerRead(serverUrl, accessToken, DockerRoutes.CONTAINERS, DockerWire::containers)

    override suspend fun dockerImages(serverUrl: String, accessToken: String): ApiResult<List<DockerImage>> =
        dockerRead(serverUrl, accessToken, DockerRoutes.IMAGES, DockerWire::images)

    override suspend fun dockerNetworks(serverUrl: String, accessToken: String): ApiResult<List<DockerNetwork>> =
        dockerRead(serverUrl, accessToken, DockerRoutes.NETWORKS, DockerWire::networks)

    override suspend fun dockerVolumes(serverUrl: String, accessToken: String): ApiResult<List<DockerVolume>> =
        dockerRead(serverUrl, accessToken, DockerRoutes.VOLUMES, DockerWire::volumes)

    override suspend fun dockerVolumeDetails(serverUrl: String, accessToken: String, name: String): ApiResult<DockerVolumeDetails> =
        dockerRead(serverUrl, accessToken, DockerRoutes.volumeDetails(name), DockerWire::volumeDetails)

    override suspend fun dockerStacks(serverUrl: String, accessToken: String): ApiResult<List<DockerStack>> =
        dockerRead(serverUrl, accessToken, DockerRoutes.STACKS, DockerWire::stacks)

    override suspend fun dockerStackServices(serverUrl: String, accessToken: String, name: String): ApiResult<List<DockerStackService>> =
        dockerRead(serverUrl, accessToken, DockerRoutes.stackServices(name), DockerWire::services)

    override suspend fun dockerContainerLogs(serverUrl: String, accessToken: String, id: String, tail: Int): ApiResult<DockerLogs> =
        dockerRead(serverUrl, accessToken, DockerRoutes.containerLogs(id, tail.coerceIn(1, 500)), DockerWire::logs)

    override suspend fun dockerStackAction(serverUrl: String, accessToken: String, name: String, action: String, confirmed: Boolean, idempotencyKey: String): ApiResult<DockerStackOperation> =
        dockerStackMutation("POST", serverUrl, DockerRoutes.stackAction(name, action), accessToken,
            JsonBody().bool("confirmed", confirmed), idempotencyKey, DockerWire::stackOperation)

    override suspend fun dockerStackPreview(serverUrl: String, accessToken: String, name: String, composeYaml: String): ApiResult<DockerStackPreview> =
        dockerStackMutation("POST", serverUrl, DockerRoutes.STACK_PREVIEW, accessToken,
            JsonBody().string("name", name.trim()).string("composeYaml", composeYaml), null, DockerWire::preview)

    override suspend fun dockerStackDeploy(serverUrl: String, accessToken: String, name: String, composeYaml: String, definitionVersion: String, idempotencyKey: String): ApiResult<DockerStackOperation> {
        // The definition is a nested object: it is what the server parses and what the version refers to.
        val definition = JSONObject().put("name", name.trim()).put("composeYaml", composeYaml).toString()
        return dockerStackMutation("POST", serverUrl, DockerRoutes.STACK_DEPLOY, accessToken,
            JsonBody().raw("definition", definition).string("definitionVersion", definitionVersion),
            idempotencyKey, DockerWire::stackOperation)
    }

    override suspend fun dockerStackOperations(serverUrl: String, accessToken: String, name: String, limit: Int): ApiResult<List<DockerStackOperation>> =
        dockerRead(serverUrl, accessToken, DockerRoutes.stackOperations(name, limit.coerceIn(1, 100)), DockerWire::stackOperations)

    override suspend fun dockerStackOperation(serverUrl: String, accessToken: String, operationId: String): ApiResult<DockerStackOperation> =
        dockerRead(serverUrl, accessToken, DockerRoutes.stackOperation(operationId), DockerWire::stackOperation)

    override suspend fun dockerStackOperationDiagnostics(serverUrl: String, accessToken: String, operationId: String): ApiResult<DockerStackOperationDiagnostics> =
        dockerRead(serverUrl, accessToken, DockerRoutes.stackOperationDiagnostics(operationId), DockerWire::diagnostics)

    override suspend fun dockerStackOperationCancel(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<DockerStackOperation> =
        dockerStackMutation("POST", serverUrl, DockerRoutes.stackOperationCancel(operationId), accessToken,
            JsonBody(), idempotencyKey, DockerWire::stackOperation)

    override suspend fun deploymentApplications(serverUrl: String, accessToken: String): ApiResult<List<DeploymentApplication>> =
        deploymentRead(serverUrl, accessToken, ApplicationDeploymentRoutes.APPLICATIONS, ApplicationDeploymentWire::applications)

    override suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String): ApiResult<DeploymentSnapshot> =
        deploymentRead(serverUrl, accessToken, ApplicationDeploymentRoutes.application(applicationId), ApplicationDeploymentWire::snapshot)

    override suspend fun deploymentRuntime(serverUrl: String, accessToken: String): ApiResult<DeploymentRuntime> =
        deploymentRead(serverUrl, accessToken, ApplicationDeploymentRoutes.RUNTIME, ApplicationDeploymentWire::runtime)

    override suspend fun deploymentTemplates(serverUrl: String, accessToken: String): ApiResult<List<DeploymentTemplate>> =
        deploymentRead(serverUrl, accessToken, ApplicationDeploymentRoutes.TEMPLATES, ApplicationDeploymentWire::templates)

    override suspend fun applicationCatalog(serverUrl: String, accessToken: String): ApiResult<List<CatalogTemplate>> =
        deploymentRead(serverUrl, accessToken, ApplicationDeploymentRoutes.CATALOG, ApplicationDeploymentWire::catalog)

    override suspend fun previewCatalogUpdate(serverUrl: String, accessToken: String, applicationId: String, templateVersion: String): ApiResult<CatalogApplicationUpdatePreview> =
        deploymentRead(serverUrl, accessToken, "${ApplicationDeploymentRoutes.catalogUpdate(applicationId)}?templateVersion=${encode(templateVersion)}", CatalogApplicationUpdateWire::preview)

    override suspend fun updateCatalogApplication(serverUrl: String, accessToken: String, preview: CatalogApplicationUpdatePreview,
        idempotencyKey: String): ApiResult<DeploymentOperation> = deploymentMutation("POST", serverUrl,
            ApplicationDeploymentRoutes.catalogUpdate(preview.applicationId), accessToken,
            JsonBody().string("templateId", preview.target.id).string("templateVersion", preview.target.version)
                .string("expectedUpdatedAt", preview.expectedUpdatedAt).raw("expectedRevisionId", preview.expectedRevisionId?.let(JSONObject::quote) ?: "null")
                .string("currentTemplateVersion", preview.currentTemplateVersion).bool("confirmed", true), idempotencyKey, ApplicationDeploymentWire::acceptedOperation)

    override suspend fun installCatalogApplication(serverUrl: String, accessToken: String, template: CatalogTemplate, name: String, hostPort: Int,
        fields: List<CatalogFieldValue>, idempotencyKey: String): ApiResult<DeploymentOperation> {
        val fieldJson = JSONArray().apply { fields.forEach { put(JSONObject().put("id", it.id).put("value", it.value)) } }
        val body = JsonBody().string("templateId", template.id).string("templateVersion", template.version).string("name", name.trim()).int("hostPort", hostPort)
            .raw("fields", fieldJson.toString()).bool("confirmed", true)
        return deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.CATALOG_INSTALL, accessToken, body, idempotencyKey,
            ApplicationDeploymentWire::catalogInstall)
    }

    override suspend fun uploadDeploymentArchive(
        serverUrl: String,
        accessToken: String,
        fileName: String,
        contentLength: Long?,
        open: () -> InputStream,
    ): ApiResult<DeploymentArchive> = withContext(Dispatchers.IO) {
        var connection: HttpURLConnection? = null
        try {
            val boundary = "RelaxKonOS-deployment-${System.currentTimeMillis()}"
            val header = buildMultipartHeader(boundary, fileName)
            val footer = "\r\n--$boundary--\r\n".toByteArray(Charsets.UTF_8)
            connection = openConnection(serverUrl, ApplicationDeploymentRoutes.UPLOADS, "POST", accessToken)
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
            if (contentLength != null && contentLength >= 0) {
                val total = header.size.toLong() + contentLength + footer.size
                if (total <= Int.MAX_VALUE) connection.setFixedLengthStreamingMode(total.toInt()) else connection.setFixedLengthStreamingMode(total)
            } else connection.setChunkedStreamingMode(BUFFER_SIZE)
            connection.outputStream.use { output ->
                output.write(header)
                open().use { input ->
                    val buffer = ByteArray(BUFFER_SIZE)
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        output.write(buffer, 0, count)
                    }
                }
                output.write(footer)
            }
            val code = connection.responseCode
            if (code !in 200..299) return@withContext readProblem(connection, code)
            runCatching { ApplicationDeploymentWire.archive(connection.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed deployment archive response.") })
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            ApiResult.Transport(error.message)
        } finally {
            connection?.disconnect()
        }
    }

    override suspend fun stageServerDeploymentArchive(
        serverUrl: String,
        accessToken: String,
        path: String,
    ): ApiResult<DeploymentArchive> = when (val result = execute(
        "POST",
        serverUrl,
        ApplicationDeploymentRoutes.FILE_REFERENCES,
        accessToken,
        JsonBody().string("path", path),
    )) {
        is ApiResult.Success -> runCatching { ApplicationDeploymentWire.archive(result.value) }
            .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed deployment archive response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }

    override suspend fun deploymentLogs(serverUrl: String, accessToken: String, applicationId: String, tail: Int): ApiResult<DeploymentLog> =
        deploymentRead(serverUrl, accessToken, ApplicationDeploymentRoutes.logs(applicationId, tail), ApplicationDeploymentWire::logs)

    override suspend fun createImageDeployment(
        serverUrl: String,
        accessToken: String,
        definition: ImageDeploymentDefinition,
        idempotencyKey: String,
    ): ApiResult<DeploymentApplication> {
        val body = JsonBody()
            .string("name", definition.name.trim())
            .string("sourceKind", "image")
            .string("workloadKind", definition.workloadKind)
            .string("readinessLevel", definition.readinessLevel)
            .string("healthCheckPath", definition.healthCheckPath.takeIf { definition.readinessLevel == "http" })
            .int("containerPort", definition.containerPort)
            .string("bindAddress", definition.bindAddress)
            .raw("hostPort", definition.hostPort?.toString() ?: "null")
            .raw("limits", deploymentLimits(definition.limits))
            .raw("volumes", deploymentVolumes(definition.volumes))
            .raw("configuration", deploymentConfiguration(definition.configuration))
            .string("siteId", definition.siteId)
        return deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.APPLICATIONS, accessToken, body, idempotencyKey,
            ApplicationDeploymentWire::createdApplication)
    }

    override suspend fun updateDeploymentDefinition(
        serverUrl: String, accessToken: String, applicationId: String, definition: DeploymentDefinitionUpdate, idempotencyKey: String,
    ): ApiResult<DeploymentApplication> = deploymentMutation("PUT", serverUrl, ApplicationDeploymentRoutes.application(applicationId),
        accessToken, DeploymentDefinitionWire.body(definition), idempotencyKey, ApplicationDeploymentWire::createdApplication)

    override suspend fun deploymentImageTags(serverUrl: String, accessToken: String, repository: String): ApiResult<DeploymentImageTags> =
        deploymentRead(serverUrl, accessToken,
            "${ApplicationDeploymentRoutes.IMAGE_TAGS}?repository=${URLEncoder.encode(repository.trim(), "UTF-8")}",
            ApplicationDeploymentWire::imageTags)

    override suspend fun deploymentOperationDiagnostics(serverUrl: String, accessToken: String, operationId: String): ApiResult<DeploymentOperationDiagnostics> =
        deploymentRead(serverUrl, accessToken, ApplicationDeploymentRoutes.operationLogs(operationId),
            ApplicationDeploymentWire::operationDiagnostics)
    override suspend fun deploymentOperation(serverUrl: String, accessToken: String, operationId: String): ApiResult<DeploymentOperation> =
        deploymentRead(serverUrl, accessToken, ApplicationDeploymentRoutes.operation(operationId),
            ApplicationDeploymentWire::acceptedOperation)

    override suspend fun createArchiveDeployment(
        serverUrl: String,
        accessToken: String,
        definition: ArchiveDeploymentDefinition,
        idempotencyKey: String,
    ): ApiResult<DeploymentApplication> {
        val body = JsonBody()
            .string("name", definition.name.trim())
            .string("sourceKind", definition.sourceKind)
            .string("workloadKind", definition.workloadKind)
            .string("readinessLevel", definition.readinessLevel)
            .string("healthCheckPath", definition.healthCheckPath)
            .int("containerPort", definition.containerPort)
            .raw("hostPort", definition.hostPort?.toString() ?: "null")
            .string("bindAddress", definition.bindAddress)
            .raw("limits", deploymentLimits(definition.limits))
            .raw("volumes", deploymentVolumes(definition.volumes))
            .raw("configuration", deploymentConfiguration(definition.configuration))
            .string("siteId", definition.siteId)
        return deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.APPLICATIONS, accessToken, body, idempotencyKey,
            ApplicationDeploymentWire::createdApplication)
    }

    override suspend fun deployArchive(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        archiveReferenceId: String,
        definition: ArchiveDeploymentDefinition,
        expectedUpdatedAt: String,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> {
        val source = JSONObject().apply {
            put("archiveReferenceId", archiveReferenceId)
            definition.baseImage?.trim()?.takeIf(String::isNotEmpty)?.let { put("baseImage", it) }
            definition.runtimeVersion?.trim()?.takeIf(String::isNotEmpty)?.let { put("runtimeVersion", it) }
            definition.programEntry?.trim()?.takeIf(String::isNotEmpty)?.let { put("programEntry", it) }
            if (definition.arguments.isNotEmpty()) put("arguments", JSONArray(definition.arguments))
            if (definition.selfContained) put("selfContained", true)
        }.toString()
        return deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.deploy(applicationId), accessToken,
            JsonBody().raw("source", source).string("expectedUpdatedAt", expectedUpdatedAt).bool("confirmed", true), idempotencyKey, ApplicationDeploymentWire::acceptedOperation)
    }

    override suspend fun deployRevision(serverUrl: String, accessToken: String, applicationId: String,
        source: DeploymentRevisionSource, expectedUpdatedAt: String, idempotencyKey: String): ApiResult<DeploymentOperation> =
        deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.deploy(applicationId), accessToken,
            JsonBody().raw("source", source.json()).string("expectedUpdatedAt", expectedUpdatedAt).bool("confirmed", true), idempotencyKey, ApplicationDeploymentWire::acceptedOperation)

    override suspend fun deployImage(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        imageReference: String,
        expectedUpdatedAt: String,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> {
        val source = JSONObject().put("imageReference", imageReference.trim()).toString()
        val body = JsonBody().raw("source", source).string("expectedUpdatedAt", expectedUpdatedAt).bool("confirmed", true)
        return deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.deploy(applicationId), accessToken, body, idempotencyKey,
            ApplicationDeploymentWire::acceptedOperation)
    }

    override suspend fun rollbackDeployment(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        revisionId: String,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> {
        val body = JsonBody().string("revisionId", UUID.fromString(revisionId).toString()).bool("confirmed", true)
        return deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.rollback(applicationId), accessToken, body, idempotencyKey,
            ApplicationDeploymentWire::acceptedOperation)
    }

    override suspend fun deleteDeployment(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> = deploymentMutation(
        "DELETE", serverUrl, ApplicationDeploymentRoutes.delete(applicationId), accessToken,
        JsonBody().bool("deleteVolumes", false).bool("confirmed", false), idempotencyKey,
        ApplicationDeploymentWire::acceptedOperation,
    )

    override suspend fun deploymentLifecycle(
        serverUrl: String,
        accessToken: String,
        applicationId: String,
        action: DeploymentLifecycleAction,
        idempotencyKey: String,
    ): ApiResult<DeploymentOperation> = deploymentMutation(
        "POST", serverUrl, ApplicationDeploymentRoutes.lifecycle(applicationId, action), accessToken,
        JsonBody().bool("force", false).bool("confirmed", true), idempotencyKey,
        ApplicationDeploymentWire::acceptedOperation,
    )

    override suspend fun cancelDeploymentOperation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<DeploymentOperation> =
        deploymentMutation("POST", serverUrl, ApplicationDeploymentRoutes.cancelOperation(operationId), accessToken,
            JsonBody(), idempotencyKey, ApplicationDeploymentWire::acceptedOperation)

    private suspend fun <T> deploymentRead(serverUrl: String, accessToken: String, route: String, parse: (String) -> T): ApiResult<T> =
        when (val result = execute("GET", serverUrl, route, accessToken, null)) {
            is ApiResult.Success -> runCatching { parse(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed deployment response.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    private suspend fun <T> dockerRead(serverUrl: String, accessToken: String, route: String, parse: (String) -> T): ApiResult<T> =
        when (val result = execute("GET", serverUrl, route, accessToken, null)) {
            is ApiResult.Success -> runCatching { parse(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed Docker response.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    private suspend fun <T> webPublishingRead(serverUrl: String, accessToken: String, route: String, parse: (String) -> T): ApiResult<T> =
        webPublishingCall("GET", serverUrl, route, accessToken, null, parse)

    private suspend fun <T> webPublishingCall(
        method: String, serverUrl: String, route: String, accessToken: String, body: JsonBody?, parse: (String) -> T,
    ): ApiResult<T> = when (val result = execute(method, serverUrl, route, accessToken, body)) {
        is ApiResult.Success -> runCatching { parse(result.value) }
            .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed web publishing response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }

    private suspend fun <T> webPublishingMutation(
        serverUrl: String, accessToken: String, route: String, body: JsonBody, idempotencyKey: String, parse: (String) -> T,
    ): ApiResult<T> = when (val result = execute("POST", serverUrl, route, accessToken, body, mapOf("Idempotency-Key" to idempotencyKey))) {
        is ApiResult.Success -> runCatching { parse(result.value) }
            .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed website publication operation.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }

    private suspend fun dockerMutation(method: String, serverUrl: String, route: String, accessToken: String, body: JsonBody): ApiResult<DockerOperation> =
        when (val result = execute(method, serverUrl, route, accessToken, body)) {
            is ApiResult.Success -> runCatching { DockerWire.operation(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed Docker operation response.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    /**
     * A stack mutation carries an idempotency key, so a retry after an unanswered request returns the
     * operation that was already created instead of starting a second one. The response is a durable
     * operation rather than a result, and it is `202`, which the transport still reads as success.
     */
    private suspend fun <T> dockerStackMutation(
        method: String, serverUrl: String, route: String, accessToken: String, body: JsonBody,
        idempotencyKey: String?, parse: (String) -> T,
    ): ApiResult<T> {
        val headers = if (idempotencyKey == null) emptyMap() else mapOf("Idempotency-Key" to idempotencyKey)
        return when (val result = execute(method, serverUrl, route, accessToken, body, headers)) {
            is ApiResult.Success -> runCatching { parse(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed Docker stack response.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }

    private suspend fun <T> deploymentMutation(
        method: String, serverUrl: String, route: String, accessToken: String, body: JsonBody, idempotencyKey: String,
        parse: (String) -> T,
    ): ApiResult<T> = when (val result = execute(method, serverUrl, route, accessToken, body, mapOf("Idempotency-Key" to idempotencyKey))) {
        is ApiResult.Success -> runCatching { parse(result.value) }
            .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed deployment response.") })
        is ApiResult.Problem -> result
        is ApiResult.Transport -> result
    }

    private fun deploymentConfiguration(entries: List<DeploymentConfigEntry>): String = JSONArray().apply {
        entries.forEach { entry -> put(JSONObject().put("name", entry.name.trim()).put("value", entry.value).put("isSecret", entry.isSecret)) }
    }.toString()

    private fun deploymentVolumes(volumes: List<DeploymentVolume>): String = JSONArray().apply {
        volumes.forEach { put(JSONObject().put("name", it.name).put("containerPath", it.containerPath).put("readOnly", it.readOnly)) }
    }.toString()

    private fun deploymentLimits(limits: DeploymentLimits?): String = limits?.let {
        JSONObject().put("cpuCores", it.cpuCores).put("memoryBytes", it.memoryBytes).put("pidsLimit", it.pidsLimit).toString()
    } ?: "null"

    override suspend fun login(serverUrl: String, identifier: String, password: CharArray): ApiResult<LoginSession> {
        val body = JsonBody()
            .string("identifier", identifier.trim())
            .secret("password", password)
            .string("clientPlatform", CLIENT_PLATFORM)
            .string("deviceName", deviceName)
            .string("clientVersion", clientVersion)
        return when (val parsed = execute("POST", serverUrl, AuthRoutes.LOGIN, accessToken = null, body = body)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> parseLogin(parsed.value)
        }
    }

    /**
     * Asks a server what its host runs on, without a credential and without a session.
     *
     * A missing or non-string `kind` is a contract break ([ApiResult.Transport]); a *name* this build
     * does not know is [HostOperatingSystemKind.Unknown], because a newer server naming a system this
     * version has no mark for is a normal answer, not a reason to fail a list row.
     */
    override suspend fun hostOperatingSystem(serverUrl: String): ApiResult<HostOperatingSystemKind> =
        when (val parsed = execute("GET", serverUrl, ServerHostRoutes.OPERATING_SYSTEM, accessToken = null, body = null)) {
            is ApiResult.Success -> runCatching {
                HostOperatingSystemKind.fromWire(JSONObject(parsed.value).getString("kind"))
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed host operating system response.") })
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
        }

    override suspend fun acceptOwnerDeviceInvitation(
        serverUrl: String,
        invitationToken: String,
        deviceName: String,
        publicKeySpki: String,
    ): ApiResult<OwnerDeviceEnrollment> {
        val body = JsonBody()
            .string("token", invitationToken)
            .string("deviceName", deviceName)
            .string("platform", CLIENT_PLATFORM)
            .string("publicKeySpki", publicKeySpki)
            .string("clientVersion", clientVersion)
        return when (val parsed = execute("POST", serverUrl, OwnerDeviceRoutes.ACCEPT_INVITATION, accessToken = null, body = body)) {
            is ApiResult.Success -> runCatching {
                OwnerDeviceEnrollment(JSONObject(parsed.value).getString("id"))
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed owner-device enrollment response.") })
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
        }
    }

    override suspend fun ownerDeviceChallenge(serverUrl: String, deviceId: String): ApiResult<OwnerDeviceChallenge> {
        val body = JsonBody().string("deviceId", deviceId)
        return when (val parsed = execute("POST", serverUrl, OwnerDeviceRoutes.CHALLENGE, accessToken = null, body = body)) {
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                OwnerDeviceChallenge(json.getString("challengeId"), json.getString("nonce"))
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed owner-device challenge response.") })
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
        }
    }

    override suspend fun signInWithOwnerDevice(
        serverUrl: String,
        challengeId: String,
        deviceId: String,
        signature: String,
    ): ApiResult<LoginSession> {
        val body = JsonBody()
            .string("challengeId", challengeId)
            .string("deviceId", deviceId)
            .string("signature", signature)
        return when (val parsed = execute("POST", serverUrl, OwnerDeviceRoutes.SIGN_IN, accessToken = null, body = body)) {
            is ApiResult.Success -> parseLogin(parsed.value)
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
        }
    }

    override suspend fun refresh(serverUrl: String, refreshToken: String): ApiResult<AuthTokens> {
        val body = JsonBody().string("refreshToken", refreshToken)
        return when (val parsed = execute("POST", serverUrl, AuthRoutes.REFRESH, accessToken = null, body = body)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                AuthTokens(
                    accessToken = json.getString("accessToken"),
                    refreshToken = json.getString("refreshToken"),
                    accessTokenExpiresAtMillis = IsoInstant.toEpochMillis(json.optString("accessTokenExpiresAt")),
                    refreshTokenExpiresAtMillis = IsoInstant.toEpochMillis(json.optString("refreshTokenExpiresAt")),
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed refresh response.") })
        }
    }

    override suspend fun logout(serverUrl: String, accessToken: String, refreshToken: String): ApiResult<Unit> {
        val body = JsonBody().string("refreshToken", refreshToken)
        return execute("POST", serverUrl, AuthRoutes.LOGOUT, accessToken, body).asUnit()
    }

    /** One-shot host elevation request. The password never outlives this call. */
    override suspend fun requestElevation(
        serverUrl: String,
        accessToken: String,
        capability: String,
        target: String,
        password: CharArray?,
        administratorUsername: String?,
    ): ApiResult<ElevationGrant> {
        val body = JsonBody()
            .string("capability", capability)
            .string("target", target)
            .nullableSecret("password", password)
            .string("administratorUsername", administratorUsername)
            .bool("includeDescendants", false)
        return when (val parsed = execute("POST", serverUrl, PrivilegedRoutes.ELEVATION, accessToken, body)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                ElevationGrant(json.optBoolean("elevated"), IsoInstant.toEpochMillis(json.optString("expiresAt")))
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed elevation response.") })
        }
    }

    /**
     * One-shot file elevation request. Mutating operations grant their directory scope through
     * `includeDescendants` because the target itself may not be readable yet.
     */
    override suspend fun requestFileElevation(
        serverUrl: String,
        accessToken: String,
        path: String,
        capability: String?,
        password: CharArray?,
        relatedPaths: List<String>,
        includeDescendants: Boolean,
        administratorUsername: String?,
    ): ApiResult<FileElevationGrant> {
        val body = JsonBody()
            .string("path", path)
            .string("capability", capability)
            .nullableSecret("password", password)
            .string("administratorUsername", administratorUsername)
            .bool("includeDescendants", includeDescendants)
        if (relatedPaths.isNotEmpty()) {
            body.raw("relatedPaths", jsonStringArray(relatedPaths))
        }
        return when (val parsed = execute("POST", serverUrl, FileRoutes.ELEVATION, accessToken, body)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                FileElevationGrant(
                    requiresElevation = json.optBoolean("requiresElevation"),
                    elevated = json.optBoolean("elevated"),
                    expiresAtMillis = IsoInstant.toEpochMillis(json.optString("expiresAt")),
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed file elevation response.") })
        }
    }

    override suspend fun terminalSettings(serverUrl: String, accessToken: String, workspaceId: String): ApiResult<TerminalSettings> =
        webPublishingRead(serverUrl, accessToken, TerminalSettingsWire.route(workspaceId), TerminalSettingsWire::parse)

    override suspend fun saveTerminalSettings(serverUrl: String, accessToken: String, workspaceId: String, settings: TerminalSettings): ApiResult<TerminalSettings> =
        when (val result = execute("PUT", serverUrl, TerminalSettingsWire.route(workspaceId), accessToken, TerminalSettingsWire.body(settings))) {
            is ApiResult.Success -> runCatching { TerminalSettingsWire.parse(result.value) }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed terminal settings.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    override suspend fun listDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<DirectoryListing> {
        val query = "?path=" + encode(path)
        return when (val parsed = execute("GET", serverUrl, FileRoutes.LIST + query, accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching {
                val json = JSONObject(parsed.value)
                val entries = mutableListOf<RemoteEntry>()
                val directories = json.optJSONArray("directories")
                for (index in 0 until (directories?.length() ?: 0)) {
                    val item = directories!!.getJSONObject(index)
                    entries += RemoteEntry(
                        path = item.getString("path"),
                        name = item.getString("name"),
                        isDirectory = true,
                        sizeBytes = item.optNullableLong("size"),
                        modifiedAtMillis = IsoInstant.toEpochMillis(item.optString("modified")),
                        mimeType = null,
                        isHidden = item.getBoolean("isHidden"),
                        isSystem = item.getBoolean("isSystem"),
                        isDrive = item.getString("type") == "drive",
                    )
                }
                val files = json.optJSONArray("files")
                for (index in 0 until (files?.length() ?: 0)) {
                    val item = files!!.getJSONObject(index)
                    entries += RemoteEntry(
                        path = item.getString("path"),
                        name = item.getString("name"),
                        isDirectory = false,
                        sizeBytes = item.optNullableLong("size"),
                        modifiedAtMillis = IsoInstant.toEpochMillis(item.optString("modified")),
                        mimeType = item.optNullableString("mimeType"),
                        isHidden = item.getBoolean("isHidden"),
                        isSystem = item.getBoolean("isSystem"),
                    )
                }
                DirectoryListing(
                    path = json.optString("path", path),
                    name = json.optString("name"),
                    entries = entries.sortedWith(
                        compareByDescending<RemoteEntry> { it.isDirectory }.thenBy { it.name.lowercase() },
                    ),
                )
            }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed directory listing.") })
        }
    }

    override suspend fun fileProperties(serverUrl: String, accessToken: String, path: String): ApiResult<RemoteFileProperties> {
        val query = "?path=" + encode(path)
        return when (val parsed = execute("GET", serverUrl, FileRoutes.PROPERTIES + query, accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> parseFileProperties(parsed.value)
        }
    }

    override suspend fun setFilePermissions(serverUrl: String, accessToken: String, path: String, unixMode: Int): ApiResult<RemoteFileProperties> =
        when (val result = execute("PUT", serverUrl, FileRoutes.PERMISSIONS, accessToken,
            JsonBody().string("path", path).int("unixMode", unixMode))) {
            is ApiResult.Success -> parseFileProperties(result.value)
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    private fun parseFileProperties(raw: String): ApiResult<RemoteFileProperties> = runCatching {
        val json = JSONObject(raw)
        require(json.has("unixMode"))
        require(json.getString("type") in setOf("file", "directory", "drive"))
        RemoteFileProperties(
            path = json.getString("path"), name = json.getString("name"),
            isDirectory = json.getString("type") != "file",
            sizeBytes = json.optNullableLong("size"),
            createdMillis = IsoInstant.toEpochMillis(json.optString("created")),
            modifiedMillis = IsoInstant.toEpochMillis(json.optString("modified")),
            permissions = json.getString("permissions"),
            unixMode = if (json.isNull("unixMode")) null else json.getInt("unixMode").also { require(it in 0..0xfff) },
            accessedMillis = IsoInstant.toEpochMillis(json.optString("accessed")),
            attributes = json.getString("attributes"),
        )
    }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed file properties.") })

    override suspend fun createDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> =
        execute("POST", serverUrl, FileRoutes.DIRECTORY + "?path=" + encode(path), accessToken, null).asUnit()

    override suspend fun delete(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> =
        execute("DELETE", serverUrl, FileRoutes.DELETE + "?path=" + encode(path), accessToken, null).asUnit()

    override suspend fun rename(serverUrl: String, accessToken: String, sourcePath: String, newName: String): ApiResult<Unit> {
        val body = JsonBody().string("sourcePath", sourcePath).string("newName", newName)
        return execute("POST", serverUrl, FileRoutes.RENAME, accessToken, body).asUnit()
    }

    override suspend fun move(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit> {
        val body = JsonBody().string("sourcePath", sourcePath).string("destinationPath", destinationPath).bool("overwrite", false)
        return execute("POST", serverUrl, FileRoutes.MOVE, accessToken, body).asUnit()
    }

    override suspend fun copy(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit> {
        val body = JsonBody().string("sourcePath", sourcePath).string("destinationPath", destinationPath).bool("overwrite", false)
        return execute("POST", serverUrl, FileRoutes.COPY, accessToken, body).asUnit()
    }

    override suspend fun upload(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        source: InputStream,
        contentLength: Long?,
        onProgress: ((Long) -> Unit)?,
    ): ApiResult<Unit> = withContext(Dispatchers.IO) {
        var connection: HttpURLConnection? = null
        try {
            val boundary = "RelaxKonOS-${System.currentTimeMillis()}"
            val header = buildMultipartHeader(boundary, fileName)
            val footer = "\r\n--$boundary--\r\n".toByteArray(Charsets.UTF_8)
            connection = openConnection(serverUrl, FileRoutes.UPLOAD + "?path=" + encode(targetDirectoryPath), "POST", accessToken)
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
            if (contentLength != null && contentLength >= 0) {
                val total = header.size.toLong() + contentLength + footer.size
                if (total <= Int.MAX_VALUE) connection.setFixedLengthStreamingMode(total.toInt()) else connection.setFixedLengthStreamingMode(total)
            } else {
                connection.setChunkedStreamingMode(BUFFER_SIZE)
            }
            connection.outputStream.use { output ->
                output.write(header)
                source.use { input ->
                    val buffer = ByteArray(BUFFER_SIZE)
                    var written = 0L
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        output.write(buffer, 0, count)
                        written += count
                        onProgress?.invoke(written)
                    }
                }
                output.write(footer)
            }
            val code = connection.responseCode
            if (code !in 200..299) return@withContext readProblem(connection, code)
            ApiResult.Success(Unit)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            ApiResult.Transport(error.message)
        } finally {
            connection?.disconnect()
        }
    }

    override suspend fun createUploadSession(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        length: Long,
        lastModifiedMillis: Long?,
        idempotencyKey: String,
    ): ApiResult<UploadSession> {
        val body = JsonBody()
            .string("targetDirectoryPath", targetDirectoryPath)
            .string("fileName", fileName)
            .raw("length", length.toString())
            .raw("lastModifiedUtc", lastModifiedMillis?.let { "\"${IsoInstant.fromEpochMillis(it)}\"" } ?: "null")
        return when (val parsed = execute(
            "POST",
            serverUrl,
            FileRoutes.UPLOADS,
            accessToken,
            body,
            headers = mapOf(UploadProtocol.IDEMPOTENCY_KEY_HEADER to idempotencyKey),
        )) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> parseUploadSession(parsed.value)
        }
    }

    override suspend fun uploadSession(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
    ): ApiResult<UploadSession> =
        when (val parsed = execute("GET", serverUrl, FileRoutes.upload(uploadId), accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> parseUploadSession(parsed.value)
        }

    override suspend fun sendUploadChunk(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        offset: Long,
        chunkLength: Long,
        source: InputStream,
        onInFlight: ((Long) -> Unit)?,
    ): UploadChunkResult = withContext(Dispatchers.IO) {
        var connection: HttpURLConnection? = null
        try {
            // One chunk, one connection. A pooled keep-alive socket that survived a network change is
            // exactly the thing that must not be reused here, so the request never depends on one.
            connection = openConnection(
                serverUrl,
                FileRoutes.upload(uploadId),
                "PATCH",
                accessToken,
                readTimeoutMillis = CHUNK_READ_TIMEOUT_MILLIS,
            )
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/octet-stream")
            connection.setRequestProperty(UploadProtocol.OFFSET_HEADER, offset.toString())
            // Declaring the length is what gives the server a Content-Length to hold the chunk to; a
            // chunk is far below 2 GiB, so the long overload's ceiling is never reached.
            connection.setFixedLengthStreamingMode(chunkLength)
            connection.outputStream.use { output ->
                val buffer = ByteArray(BUFFER_SIZE)
                var written = 0L
                while (written < chunkLength) {
                    val count = source.read(buffer, 0, minOf(buffer.size.toLong(), chunkLength - written).toInt())
                    if (count < 0) break
                    output.write(buffer, 0, count)
                    written += count
                    onInFlight?.invoke(written)
                }
                if (written < chunkLength) {
                    // The source stopped early: the document changed under us. Sending the remainder of
                    // a file that no longer exists would publish a mixture of two versions.
                    throw TruncatedSourceException(written)
                }
            }
            val code = connection.responseCode
            val authoritative = connection.getHeaderField(UploadProtocol.OFFSET_HEADER)?.trim()?.toLongOrNull()
            if (code in 200..299) {
                // The server always names the offset on 204. The declared end is the same number and is
                // only used when something between us dropped the header, never in preference to it.
                return@withContext UploadChunkResult.Confirmed(authoritative ?: (offset + chunkLength))
            }
            val problem = readProblemBody(connection, code)
                ?: return@withContext UploadChunkResult.Unreachable("Server returned HTTP $code.")
            UploadChunkResult.Refused(code, problem.code, authoritative)
        } catch (error: CancellationException) {
            throw error
        } catch (error: TruncatedSourceException) {
            UploadChunkResult.SourceShort(error.deliveredBytes)
        } catch (error: Exception) {
            UploadChunkResult.Unreachable(error.message)
        } finally {
            connection?.disconnect()
        }
    }

    override suspend fun commitUpload(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        contentHash: String?,
    ): ApiResult<Unit> {
        val body = JsonBody().string("contentHash", contentHash)
        return execute("POST", serverUrl, FileRoutes.uploadCommit(uploadId), accessToken, body).asUnit()
    }

    override suspend fun abortUpload(serverUrl: String, accessToken: String, uploadId: String): ApiResult<Unit> =
        execute("DELETE", serverUrl, FileRoutes.upload(uploadId), accessToken, null).asUnit()

    override suspend fun performanceSnapshot(serverUrl: String, accessToken: String): ApiResult<PerformanceSnapshot> =
        readPerformance(serverUrl, accessToken, SystemRoutes.PERFORMANCE_SNAPSHOT, PerformanceWire::snapshot)

    override suspend fun performanceInfo(serverUrl: String, accessToken: String): ApiResult<PerformanceInfo> =
        readPerformance(serverUrl, accessToken, SystemRoutes.PERFORMANCE_INFO, PerformanceWire::info)

    override suspend fun performanceHistory(serverUrl: String, accessToken: String): ApiResult<List<PerformanceSnapshot>> =
        readPerformance(serverUrl, accessToken, SystemRoutes.PERFORMANCE_HISTORY + "?seconds=60", PerformanceWire::history)

    override suspend fun networkAddresses(serverUrl: String, accessToken: String): ApiResult<List<NetworkAddress>> =
        readPerformance(serverUrl, accessToken, SystemRoutes.NETWORK_ADDRESSES, PerformanceWire::addresses)

    private suspend fun <T> readPerformance(url: String, token: String, route: String, parse: (String) -> T): ApiResult<T> =
        when (val result = execute("GET", url, route, token, null)) {
            is ApiResult.Success -> runCatching { parse(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed performance response.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }

    override suspend fun queryProcesses(
        serverUrl: String,
        accessToken: String,
        page: Int,
        pageSize: Int,
        filter: String?,
        sort: ProcessSort,
        descending: Boolean,
    ): ApiResult<ProcessPage> {
        require(page > 0 && pageSize in 1..500 && (filter?.length ?: 0) <= 512)
        val query = buildString {
            append("?page=").append(page)
            append("&pageSize=").append(pageSize)
            append("&sort=").append(sort.wireName).append("&direction=").append(if (descending) "desc" else "asc")
            if (!filter.isNullOrBlank()) {
                append("&filter=").append(encode(filter))
            }
        }
        return when (val parsed = execute("GET", serverUrl, SystemRoutes.PROCESS_QUERY + query, accessToken, null)) {
            is ApiResult.Problem -> parsed
            is ApiResult.Transport -> parsed
            is ApiResult.Success -> runCatching { SystemProcessWire.page(parsed.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed process page.") })
        }
    }

    override suspend fun killProcess(serverUrl: String, accessToken: String, pid: Int, expectedStartTime: String): ApiResult<ProcessKillResult> {
        require(pid > 0); SystemProcessWire.startTime(expectedStartTime)
        return when (val result = execute("DELETE", serverUrl, SystemRoutes.processKill(pid), accessToken,
            JsonBody().string("expectedStartTime", expectedStartTime))) {
            is ApiResult.Success -> runCatching { SystemProcessWire.kill(result.value) }
                .fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed process termination receipt.") })
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }

    /** Streams a remote file into [sink]. Used by the download action. */
    override suspend fun download(
        serverUrl: String,
        accessToken: String,
        path: String,
        sink: DownloadSink,
        onProgress: ((writtenBytes: Long, totalBytes: Long?) -> Unit)?,
    ): ApiResult<Long> = streamInto(
        serverUrl = serverUrl,
        accessToken = accessToken,
        route = FileRoutes.DOWNLOAD,
        query = "?path=" + encode(path),
        sink = sink,
        onProgress = onProgress,
    )

    override suspend fun thumbnail(
        serverUrl: String,
        accessToken: String,
        path: String,
        maxEdge: Int,
    ): ApiResult<ByteArray> {
        val buffer = ByteArrayOutputStream()
        val result = streamInto(
            serverUrl = serverUrl,
            accessToken = accessToken,
            route = FileRoutes.THUMBNAIL,
            // `maxEdge` is a plain count, so it needs no encoding; the path does.
            query = "?path=" + encode(path) + "&maxEdge=" + maxEdge,
            sink = { buffer },
            onProgress = null,
        )
        // The buffer is the answer, so the byte count the transfer reported is discarded and the
        // bytes themselves are returned. A thumbnail is small by construction; that is what makes
        // holding one in memory reasonable at all.
        return when (result) {
            is ApiResult.Success -> ApiResult.Success(buffer.toByteArray())
            is ApiResult.Problem -> result
            is ApiResult.Transport -> result
        }
    }

    /**
     * Streams one GET body into [sink].
     *
     * A download and a thumbnail differ in the route they ask and what comes back; everything that
     * makes the transfer itself correct is the same, and it is the part that has to stay correct —
     * the destination is opened only after the server has accepted the request, so a refused or
     * unauthorized transfer writes nothing anywhere.
     */
    private suspend fun streamInto(
        serverUrl: String,
        accessToken: String,
        route: String,
        query: String,
        sink: DownloadSink,
        onProgress: ((writtenBytes: Long, totalBytes: Long?) -> Unit)?,
    ): ApiResult<Long> = withContext(Dispatchers.IO) {
        var connection: HttpURLConnection? = null
        try {
            connection = openConnection(serverUrl, route + query, "GET", accessToken)
            connection.connect()
            val code = connection.responseCode
            if (code !in 200..299) {
                return@withContext readProblem(connection, code)
            }
            val total = connection.contentLengthLong.takeIf { it >= 0 }
            val written = connection.inputStream.use { input ->
                sink.open().use { output ->
                    val buffer = ByteArray(BUFFER_SIZE)
                    var copied = 0L
                    while (true) {
                        kotlinx.coroutines.currentCoroutineContext().ensureActive()
                        val count = input.read(buffer)
                        if (count < 0) break
                        kotlinx.coroutines.currentCoroutineContext().ensureActive()
                        output.write(buffer, 0, count)
                        copied += count
                        onProgress?.invoke(copied, total)
                    }
                    copied
                }
            }
            ApiResult.Success(written)
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            ApiResult.Transport(error.message)
        } finally {
            connection?.disconnect()
        }
    }

    private suspend fun execute(
        method: String,
        serverUrl: String,
        path: String,
        accessToken: String?,
        body: JsonBody?,
        headers: Map<String, String> = emptyMap(),
    ): ApiResult<String> = withContext(Dispatchers.IO) {
        var connection: HttpURLConnection? = null
        try {
            connection = openConnection(serverUrl, path, method, accessToken)
            connection.instanceFollowRedirects = false
            for ((name, value) in headers) {
                connection.setRequestProperty(name, value)
            }
            if (body != null) {
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                connection.connect()
                connection.outputStream.use { output: OutputStream -> body.writeTo(output) }
            } else {
                connection.connect()
            }
            val code = connection.responseCode
            if (code !in 200..299) {
                return@withContext readProblem(connection, code)
            }
            ApiResult.Success(connection.inputStream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty())
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            ApiResult.Transport(error.message)
        } finally {
            connection?.disconnect()
        }
    }

    private fun openConnection(
        serverUrl: String,
        path: String,
        method: String,
        accessToken: String?,
        readTimeoutMillis: Int = READ_TIMEOUT_MILLIS,
    ): HttpURLConnection {
        val url = URL(serverUrl.trim().trimEnd('/') + path)
        return (url.openConnection() as HttpURLConnection).apply {
            ServerCertificateTrust.configure(this)
            instanceFollowRedirects = false
            requestMethod = method
            connectTimeout = CONNECT_TIMEOUT_MILLIS
            readTimeout = readTimeoutMillis
            setRequestProperty("Accept", "application/json")
            if (accessToken != null) {
                setRequestProperty("Authorization", "Bearer $accessToken")
            }
        }
    }

    /**
     * Turns a non-2xx response into a verdict.
     *
     * The 4xx/5xx split is decided by [readsAsProblem]: a 4xx body is always a Problem, a 5xx body only
     * when the server named a RelaxKonOS problem code. Bodyless or non-JSON 4xx responses still carry
     * their HTTP verdict, including authorization middleware responses.
     */
    private fun readProblem(connection: HttpURLConnection, code: Int): ApiResult<Nothing> {
        val problem = readProblemBody(connection, code)
            // Authorization middleware may answer without a ProblemDetails body. Preserve the
            // HTTP verdict so a 401 refreshes the session and a 403 is shown as a permission error.
            ?: return if (code in 400..499) ApiResult.Problem(code, "", null)
                else ApiResult.Transport("Server returned HTTP $code.")
        return ApiResult.Problem(status = code, code = problem.code, traceId = problem.traceId)
    }

    /**
     * Reads a ProblemDetails body, or `null` when the response is not a contract answer.
     *
     * Split out of [readProblem] because the chunk route needs the code *and* keeps the connection open
     * to read the authoritative `Upload-Offset` header, which a sealed [ApiResult] cannot carry.
     */
    private fun readProblemBody(connection: HttpURLConnection, status: Int): ProblemBody? {
        val text = connection.errorStream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()
        val json = runCatching { JSONObject(text) }.getOrNull() ?: return null
        val type = json.optNullableString("type")
        val problemCode = json.optNullableString("problemCode")
        if (!readsAsProblem(status, type, problemCode)) {
            return null
        }
        return ProblemBody(code = ProblemCodes.from(type, problemCode), traceId = json.optNullableString("traceId"))
    }

    private fun parseUploadSession(payload: String): ApiResult<UploadSession> = runCatching {
        val json = JSONObject(payload)
        UploadSession(
            uploadId = json.getString("uploadId"),
            offset = json.getLong("offset"),
            length = json.getLong("length"),
            chunkSize = json.optInt("chunkSize", UploadProtocol.DEFAULT_CHUNK_SIZE),
            elevated = json.optBoolean("elevated"),
            expiresAtMillis = IsoInstant.toEpochMillis(json.optString("expiresAt")),
        )
    }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed upload session.") })

    private fun parseLogin(payload: String): ApiResult<LoginSession> = runCatching {
        val json = JSONObject(payload)
        val server = json.getJSONObject("server")
        val capabilities = server.optJSONArray("capabilities") ?: JSONArray()
        // Required, not optional: the answer decides whether the shell warns before the first folder
        // open, and a missing one is a contract break rather than "assume usable".
        val eligibility = json.getJSONObject("executionEligibility")
        LoginSession(
            // `UserDto` names the field `username`; reading `name` left the account blank on the home page.
            userName = json.getJSONObject("user").optString("username"),
            workspaceName = json.getJSONObject("workspace").getString("name"),
            workspaceId = json.getJSONObject("workspace").getString("id").also { require(java.util.UUID.fromString(it).toString().equals(it, ignoreCase = true)) },
            server = ServerDescriptor(
                platform = server.optString("platform"),
                capabilities = (0 until capabilities.length()).map { capabilities.getString(it) }.toSet(),
                privilegedOperations = server.optJSONObject("host")?.getJSONObject("capabilities")?.getBoolean("privilegedOperations") == true,
            ),
            tokens = json.getJSONObject("tokens").let { tokens ->
                AuthTokens(
                    accessToken = tokens.getString("accessToken"),
                    refreshToken = tokens.getString("refreshToken"),
                    accessTokenExpiresAtMillis = IsoInstant.toEpochMillis(tokens.optString("accessTokenExpiresAt")),
                    refreshTokenExpiresAtMillis = IsoInstant.toEpochMillis(tokens.optString("refreshTokenExpiresAt")),
                )
            },
            executionEligibility = ExecutionEligibility(
                available = eligibility.getBoolean("available"),
                // Null when the identity is usable, so the absent-reason case is the normal one.
                reason = eligibility.optNullableString("reason"),
                privilegedFilesAvailable = eligibility.getBoolean("privilegedFilesAvailable"),
            ),
        )
    }.fold({ ApiResult.Success(it) }, { ApiResult.Transport("Malformed login response.") })

    private fun ApiResult<String>.asUnit(): ApiResult<Unit> = when (this) {
        is ApiResult.Success -> ApiResult.Success(Unit)
        is ApiResult.Problem -> this
        is ApiResult.Transport -> this
    }

    private fun encode(value: String): String = URLEncoder.encode(value, "UTF-8")

    private fun buildMultipartHeader(boundary: String, fileName: String, field: String = "file"): ByteArray {
        // A Content-Disposition filename is a header value, not a display string. Remove control
        // characters and delimiters so a malicious provider cannot inject a second MIME header.
        val safeName = fileName.replace(Regex("[\\r\\n\\\"\\\\]"), "_")
        return (
            "--$boundary\r\nContent-Disposition: form-data; name=\"$field\"; filename=\"$safeName\"\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n"
            ).toByteArray(Charsets.UTF_8)
    }

    private companion object {
        const val CLIENT_PLATFORM = "android"
        const val CONNECT_TIMEOUT_MILLIS = 15_000
        const val READ_TIMEOUT_MILLIS = 20_000

        /**
         * Read timeout of one chunk request.
         *
         * Deliberately not the 20 s the rest of the API uses: an 8 MiB chunk on a slow mobile link
         * legitimately takes minutes, and a timeout there would abort a transfer that is making
         * progress. A stalled chunk is instead answered by the loop's backoff, which retries after
         * asking the server where it actually is.
         */
        const val CHUNK_READ_TIMEOUT_MILLIS = 300_000
        const val BUFFER_SIZE = 81_920

        fun defaultDeviceName(): String = "${Build.MANUFACTURER} ${Build.MODEL}".trim()
    }
}

/** The code and trace id of one ProblemDetails response, read while the connection is still open. */
private class ProblemBody(val code: String, val traceId: String?)

/** Route constants mirroring `AuthApiRoutes`. */
private object AuthRoutes {
    private const val V1 = "/api/v1.0"
    const val LOGIN = "$V1/auth/login"
    const val REFRESH = "$V1/auth/refresh"
    const val LOGOUT = "$V1/auth/logout"
}

/** Route constant mirroring `ServerApiRoutes.HostOperatingSystem`; the one anonymous server route. */
private object ServerHostRoutes {
    const val OPERATING_SYSTEM = "/api/v1.0/server/host-operating-system"
}

/** Route constants mirroring `OwnerDeviceKeyApiRoutes`. */
private object OwnerDeviceRoutes {
    private const val ROOT = "/api/v1.0/auth/owner-devices"
    const val CHALLENGE = "$ROOT/challenge"
    const val SIGN_IN = "$ROOT/sign-in"
    const val ACCEPT_INVITATION = "$ROOT/accept-invitation"
}

/** Route constants mirroring `PrivilegedApiRoutes`. */
private object PrivilegedRoutes {
    const val ELEVATION = "/api/v1.0/privileged/elevation"
}

/** Route constants mirroring `FileApiRoutes`. */
private object FileRoutes {
    private const val V1 = "/api/v1.0"
    const val LIST = "$V1/files/list"
    const val PROPERTIES = "$V1/files/properties"
    const val PERMISSIONS = "$V1/files/permissions"
    const val ELEVATION = "$V1/files/elevation"
    const val DIRECTORY = "$V1/files/directory"
    const val DELETE = "$V1/files"
    const val RENAME = "$V1/files/rename"
    const val MOVE = "$V1/files/move"
    const val COPY = "$V1/files/copy"
    const val UPLOAD = "$V1/files/upload"
    const val DOWNLOAD = "$V1/files/download"
    const val THUMBNAIL = "$V1/files/thumbnail"

    /** Resumable upload sessions: POST opens one, GET reads its offset, DELETE abandons it. */
    const val UPLOADS = "$V1/files/uploads"

    /**
     * The session sub-resource and its chunk route, which are the same URL under different methods.
     *
     * [uploadId] is issued by the server as 32 lowercase hex characters, so no escaping is involved;
     * it is never built from anything the user typed.
     */
    fun upload(uploadId: String) = "$UPLOADS/$uploadId"

    fun uploadCommit(uploadId: String) = "${upload(uploadId)}/commit"
}

/** Serialises one path into a JSON string literal. Paths are not secrets, so a plain builder is fine. */
private fun jsonString(value: String): String {
    val builder = StringBuilder("\"")
    for (char in value) {
        when {
            char == '"' -> builder.append("\\\"")
            char == '\\' -> builder.append("\\\\")
            char == '\n' -> builder.append("\\n")
            char == '\r' -> builder.append("\\r")
            char == '\t' -> builder.append("\\t")
            char < ' ' -> builder.append("\\u%04x".format(char.code))
            else -> builder.append(char)
        }
    }
    return builder.append('"').toString()
}

private fun jsonStringArray(values: List<String>): String = values.joinToString(",", "[", "]") { jsonString(it) }

internal fun JSONObject.optNullableLong(name: String): Long? = if (isNull(name)) null else optLong(name)

/**
 * Reads a string the server is allowed to leave empty. This is the package's only way to read an
 * optional string, so no caller can invent a second, wrong rule.
 *
 * `optString` alone cannot answer this. Android's `org.json` renders a JSON null as the four-letter
 * string "null" (`JSON.toString(JSONObject.NULL)` -> `String.valueOf(NULL)`), so a blank-check keeps
 * it and the field reaches the UI as that literal word — which is exactly what put "null" in every
 * row of the process list: a Windows server never resolves a process owner, so `userName` is
 * genuinely null on the wire. Only `isNull` separates "the server sent nothing" from "the server
 * sent a value", and it answers the same way on Android and on the reference `org.json`.
 */
internal fun JSONObject.optNullableString(name: String): String? =
    if (isNull(name)) null else optString(name).takeIf { it.isNotBlank() }

/** Route constants mirroring `SystemMonitorApiRoutes`. */
private object SystemRoutes {
    private const val V1 = "/api/v1.0"
    const val PERFORMANCE_SNAPSHOT = "$V1/system/performance/snapshot"
    const val PERFORMANCE_INFO = "$V1/system/performance/info"
    const val PERFORMANCE_HISTORY = "$V1/system/performance/history"
    const val NETWORK_ADDRESSES = "$V1/system/network-addresses"
    const val PROCESS_QUERY = "$V1/system/processes/query"

    fun processKill(pid: Int) = "$V1/system/processes/$pid"
}
