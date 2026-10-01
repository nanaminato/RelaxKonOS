package app.relaxkonos.mobile

import app.relaxkonos.mobile.core.net.*
import app.relaxkonos.mobile.core.net.ApiResult
import app.relaxkonos.mobile.core.net.DeploymentApplication
import app.relaxkonos.mobile.core.net.DeploymentLog
import app.relaxkonos.mobile.core.net.DeploymentOperation
import app.relaxkonos.mobile.core.net.DeploymentSnapshot
import app.relaxkonos.mobile.core.net.DeploymentRuntime
import app.relaxkonos.mobile.core.net.DeploymentTemplate
import app.relaxkonos.mobile.core.net.AuthTokens
import app.relaxkonos.mobile.core.net.DirectoryListing
import app.relaxkonos.mobile.core.net.DownloadSink
import app.relaxkonos.mobile.core.net.ElevationGrant
import app.relaxkonos.mobile.core.net.FileElevationGrant
import app.relaxkonos.mobile.core.net.HostOperatingSystemKind
import app.relaxkonos.mobile.core.net.LoginSession
import app.relaxkonos.mobile.core.net.PerformanceInfo
import app.relaxkonos.mobile.core.net.NetworkAddress
import app.relaxkonos.mobile.core.net.PerformanceSnapshot
import app.relaxkonos.mobile.core.net.ProcessSort
import app.relaxkonos.mobile.core.net.ProcessPage
import app.relaxkonos.mobile.core.net.RelaxKonGateway
import app.relaxkonos.mobile.core.net.RemoteFileProperties
import app.relaxkonos.mobile.core.net.ServerDescriptor
import app.relaxkonos.mobile.core.net.UploadChunkResult
import app.relaxkonos.mobile.core.net.UploadSession
import java.io.InputStream

/**
 * Scriptable [RelaxKonGateway].
 *
 * Each operation has a handler the test sets, plus counters for the calls that matter to the rules under
 * test. A handler that was not set throws, so a test cannot pass because an unexpected call quietly
 * returned a default.
 */
class FakeGateway : RelaxKonGateway {
    var onGuardianStatus: (suspend () -> ApiResult<GuardianStatus>)? = null
    var onGuardianWorkloads: (suspend () -> ApiResult<List<GuardianWorkload>>)? = null
    var onGuardianDefinition: (suspend (String) -> ApiResult<GuardianDefinitionResult>)? = null
    var onGuardianLogs: (suspend (String) -> ApiResult<List<GuardianLog>>)? = null
    var onGuardianSave: (suspend (GuardianDefinition, GuardianApproval?) -> ApiResult<GuardianDefinitionResult>)? = null
    var onGuardianAction: (suspend (String, String) -> ApiResult<GuardianOperation>)? = null
    var onGuardianDelete: (suspend (String) -> ApiResult<GuardianOperation>)? = null
    override suspend fun guardianStatus(serverUrl: String, accessToken: String) = requireHandler(onGuardianStatus, "guardianStatus")()
    override suspend fun guardianWorkloads(serverUrl: String, accessToken: String) = requireHandler(onGuardianWorkloads, "guardianWorkloads")()
    override suspend fun guardianDefinition(serverUrl: String, accessToken: String, id: String) = requireHandler(onGuardianDefinition, "guardianDefinition")(id)
    override suspend fun guardianLogs(serverUrl: String, accessToken: String, id: String) = requireHandler(onGuardianLogs, "guardianLogs")(id)
    override suspend fun guardianSave(serverUrl: String, accessToken: String, definition: GuardianDefinition, approval: GuardianApproval?) = requireHandler(onGuardianSave, "guardianSave")(definition, approval)
    override suspend fun guardianAction(serverUrl: String, accessToken: String, id: String, action: String) = requireHandler(onGuardianAction, "guardianAction")(id, action)
    override suspend fun guardianDelete(serverUrl: String, accessToken: String, id: String) = requireHandler(onGuardianDelete, "guardianDelete")(id)

    var onSmbStatus: (() -> ApiResult<SmbStatus>)? = null
    var onSmbCapabilities: (() -> ApiResult<SmbCapabilities>)? = null
    var onSmbShares: (() -> ApiResult<List<SmbShare>>)? = null
    var onSmbUsers: (() -> ApiResult<List<SmbUser>>)? = null
    var onSmbConnection: (() -> ApiResult<SmbConnection>)? = null
    var onSmbChange: ((SmbChange, CharArray?) -> ApiResult<SmbReceipt>)? = null
    var onDockerContainerDetails: (String) -> ApiResult<DockerContainerDetails> = { error("Unexpected container details") }
    var onDockerContainerStats: (String) -> ApiResult<DockerContainerStats> = { error("Unexpected stats") }
    var onDockerNetworkDetails: (String) -> ApiResult<DockerNetworkDetails> = { error("Unexpected network details") }
    var onDockerVolumeDetails: (String) -> ApiResult<DockerVolumeDetails> = { error("Unexpected volume details") }
    var onDockerResourceChange: (DockerResourceChange) -> ApiResult<DockerOperation> = { error("Unexpected resource change") }
    var onDockerContainers: () -> ApiResult<List<DockerContainer>> = { ApiResult.Transport(null) }
    var onDockerImages: () -> ApiResult<List<DockerImage>> = { ApiResult.Transport(null) }
    var onDockerNetworks: () -> ApiResult<List<DockerNetwork>> = { ApiResult.Transport(null) }
    var onDockerVolumes: () -> ApiResult<List<DockerVolume>> = { ApiResult.Transport(null) }
    override suspend fun dockerContainerDetails(serverUrl: String, accessToken: String, id: String) = onDockerContainerDetails(id)
    override suspend fun dockerContainerStats(serverUrl: String, accessToken: String, id: String) = onDockerContainerStats(id)
    override suspend fun dockerNetworkDetails(serverUrl: String, accessToken: String, id: String) = onDockerNetworkDetails(id)
    override suspend fun dockerVolumeDetails(serverUrl: String, accessToken: String, name: String) = onDockerVolumeDetails(name)
    override suspend fun dockerResourceChange(serverUrl: String, accessToken: String, change: DockerResourceChange) = onDockerResourceChange(change)
    override suspend fun dockerContainers(serverUrl: String, accessToken: String) = onDockerContainers()
    override suspend fun dockerImages(serverUrl: String, accessToken: String) = onDockerImages()
    override suspend fun dockerNetworks(serverUrl: String, accessToken: String) = onDockerNetworks()
    override suspend fun dockerVolumes(serverUrl: String, accessToken: String) = onDockerVolumes()
    var onDockerStatus: () -> ApiResult<DockerStatus> = { error("Unexpected Docker status read") }
    var onDockerEngineAction: (DockerEngineAction, Boolean) -> ApiResult<DockerEngineResult> = { _, _ -> error("Unexpected engine action") }
    var onDockerMirrors: () -> ApiResult<List<DockerImageMirror>> = { error("Unexpected mirrors read") }
    var onDockerCreateMirror: (DockerMirrorRequest) -> ApiResult<DockerImageMirror> = { error("Unexpected mirror create") }
    var onDockerUpdateMirror: (String, DockerMirrorRequest) -> ApiResult<DockerImageMirror> = { _, _ -> error("Unexpected mirror update") }
    var onDockerDeleteMirror: (String) -> ApiResult<Unit> = { error("Unexpected mirror delete") }
    var onDockerSelectMirror: (String?) -> ApiResult<Unit> = { error("Unexpected mirror select") }
    override suspend fun dockerStatus(serverUrl: String, accessToken: String) = onDockerStatus()
    override suspend fun dockerEngineAction(serverUrl: String, accessToken: String, action: DockerEngineAction, confirmed: Boolean) = onDockerEngineAction(action, confirmed)
    override suspend fun dockerMirrors(serverUrl: String, accessToken: String) = onDockerMirrors()
    override suspend fun dockerCreateMirror(serverUrl: String, accessToken: String, request: DockerMirrorRequest) = onDockerCreateMirror(request)
    override suspend fun dockerUpdateMirror(serverUrl: String, accessToken: String, id: String, request: DockerMirrorRequest) = onDockerUpdateMirror(id, request)
    override suspend fun dockerDeleteMirror(serverUrl: String, accessToken: String, id: String) = onDockerDeleteMirror(id)
    override suspend fun dockerSelectMirror(serverUrl: String, accessToken: String, id: String?) = onDockerSelectMirror(id)
    override suspend fun smbStatus(serverUrl: String, accessToken: String) = onSmbStatus?.invoke() ?: error("Unexpected SMB status")
    override suspend fun smbCapabilities(serverUrl: String, accessToken: String) = onSmbCapabilities?.invoke() ?: error("Unexpected SMB capabilities")
    override suspend fun smbShares(serverUrl: String, accessToken: String) = onSmbShares?.invoke() ?: error("Unexpected SMB shares")
    override suspend fun smbUsers(serverUrl: String, accessToken: String) = onSmbUsers?.invoke() ?: error("Unexpected SMB users")
    override suspend fun smbConnection(serverUrl: String, accessToken: String) = onSmbConnection?.invoke() ?: error("Unexpected SMB connection")
    override suspend fun smbChange(serverUrl: String, accessToken: String, change: SmbChange, password: CharArray?) = onSmbChange?.invoke(change, password) ?: error("Unexpected SMB change")
    var onAlertDetail: ((String) -> ApiResult<OperationalAlertDetail>)? = null
    var onAcknowledgeAlert: ((String) -> ApiResult<OperationalAlert>)? = null
    override suspend fun alertDetail(serverUrl: String, accessToken: String, id: String) = onAlertDetail?.invoke(id) ?: error("Unexpected alert detail")
    override suspend fun acknowledgeAlert(serverUrl: String, accessToken: String, id: String) = onAcknowledgeAlert?.invoke(id) ?: error("Unexpected alert acknowledgement")
    var onFirewallStatus: (() -> ApiResult<FirewallStatus>)? = null
    var onFirewallRules: (() -> ApiResult<List<FirewallRule>>)? = null
    var onChangeFirewall: ((FirewallChange, CharArray?) -> ApiResult<FirewallResult>)? = null
    override suspend fun firewallStatus(serverUrl: String, accessToken: String) = onFirewallStatus?.invoke() ?: error("Unexpected firewall status")
    override suspend fun firewallRules(serverUrl: String, accessToken: String) = onFirewallRules?.invoke() ?: error("Unexpected firewall rules")
    override suspend fun changeFirewall(serverUrl: String, accessToken: String, change: FirewallChange, password: CharArray?) = onChangeFirewall?.invoke(change, password) ?: error("Unexpected firewall change")
    var onProxySettings: suspend () -> ApiResult<ProxySettings> = { error("Unexpected proxy diagnostic call") }
    override suspend fun proxySettings(serverUrl: String, accessToken: String): ApiResult<ProxySettings> = onProxySettings()
    var onProxyRecovery: suspend () -> ApiResult<ProxyRecovery> = { error("Unexpected proxy diagnostic call") }
    override suspend fun proxyRecovery(serverUrl: String, accessToken: String): ApiResult<ProxyRecovery> = onProxyRecovery()
    override suspend fun proxyTun(serverUrl: String, accessToken: String): ApiResult<ProxyRecovery> = error("Unexpected proxy diagnostic call")
    override suspend fun proxyTraffic(serverUrl: String, accessToken: String): ApiResult<ProxyTraffic> = error("Unexpected proxy diagnostic call")
    var onProxyConnections: suspend () -> ApiResult<List<ProxyConnection>> = { error("Unexpected proxy diagnostic call") }
    override suspend fun proxyConnections(serverUrl: String, accessToken: String): ApiResult<List<ProxyConnection>> = onProxyConnections()
    override suspend fun proxyLogs(serverUrl: String, accessToken: String): ApiResult<List<ProxyLog>> = error("Unexpected proxy diagnostic call")
    override suspend fun proxyDns(serverUrl: String, accessToken: String): ApiResult<ProxyDns> = error("Unexpected proxy diagnostic call")
    var onProxyGeoData: suspend () -> ApiResult<ProxyGeoData> = { error("Unexpected proxy diagnostic call") }
    override suspend fun proxyGeoData(serverUrl: String, accessToken: String): ApiResult<ProxyGeoData> = onProxyGeoData()
    var onSaveProxySettings: suspend (ProxySettings) -> ApiResult<Unit> = { _ -> error("Unexpected proxy diagnostic call") }
    override suspend fun saveProxySettings(serverUrl: String, accessToken: String, settings: ProxySettings): ApiResult<Unit> = onSaveProxySettings(settings)
    override suspend fun configureProxyGeoData(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> = error("Unexpected proxy diagnostic call")
    override suspend fun closeProxyConnection(serverUrl: String, accessToken: String, id: String): ApiResult<Unit> = error("Unexpected proxy diagnostic call")
    var onProxyOverview: suspend () -> ApiResult<ProxyOverview> = { error("Unexpected proxy call") }
    override suspend fun proxyOverview(serverUrl: String, accessToken: String): ApiResult<ProxyOverview> = onProxyOverview()
    var onProxyProfiles: suspend () -> ApiResult<List<ProxyProfile>> = { error("Unexpected proxy call") }
    override suspend fun proxyProfiles(serverUrl: String, accessToken: String): ApiResult<List<ProxyProfile>> = onProxyProfiles()
    var onProxySubscriptions: suspend () -> ApiResult<List<ProxySubscription>> = { error("Unexpected proxy call") }
    override suspend fun proxySubscriptions(serverUrl: String, accessToken: String): ApiResult<List<ProxySubscription>> = onProxySubscriptions()
    var onProxyGroups: suspend () -> ApiResult<List<ProxyGroup>> = { error("Unexpected proxy call") }
    override suspend fun proxyGroups(serverUrl: String, accessToken: String): ApiResult<List<ProxyGroup>> = onProxyGroups()
    var onProxyRouting: suspend () -> ApiResult<ProxyRoutingMode> = { error("Unexpected proxy call") }
    override suspend fun proxyRouting(serverUrl: String, accessToken: String): ApiResult<ProxyRoutingMode> = onProxyRouting()
    override suspend fun proxyDownloadOptions(serverUrl: String, accessToken: String): ApiResult<Boolean> = error("Unexpected proxy call")
    var onProxyOperation: suspend (String) -> ApiResult<ProxyOperation> = { _ -> error("Unexpected proxy call") }
    override suspend fun proxyOperation(serverUrl: String, accessToken: String, id: String): ApiResult<ProxyOperation> = onProxyOperation(id)
    override suspend fun proxyDownload(serverUrl: String, accessToken: String, version: String): ApiResult<ProxyDownload> = error("Unexpected proxy call")
    var onProxyQueue: suspend (ProxyAction, String?, String) -> ApiResult<String> = { _, _, _ -> error("Unexpected proxy call") }
    override suspend fun proxyQueue(serverUrl: String, accessToken: String, action: ProxyAction, target: String?, key: String): ApiResult<String> = onProxyQueue(action, target, key)
    override suspend fun saveProxyProfile(serverUrl: String, accessToken: String, id: String?, request: ProxyProfileRequest): ApiResult<ProxyProfile> = error("Unexpected proxy call")
    override suspend fun activateProxyProfile(serverUrl: String, accessToken: String, id: String): ApiResult<ProxyProfile> = error("Unexpected proxy call")
    override suspend fun deleteProxyProfile(serverUrl: String, accessToken: String, id: String): ApiResult<Unit> = error("Unexpected proxy call")
    override suspend fun applyProxyConfiguration(serverUrl: String, accessToken: String, id: String, yaml: String): ApiResult<Unit> = error("Unexpected proxy call")
    var onImportProxySubscription: suspend (ProxyImportRequest) -> ApiResult<ProxySubscription> = { _ -> error("Unexpected proxy call") }
    override suspend fun importProxySubscription(serverUrl: String, accessToken: String, request: ProxyImportRequest): ApiResult<ProxySubscription> = onImportProxySubscription(request)
    override suspend fun selectProxyNode(serverUrl: String, accessToken: String, group: String, proxy: String): ApiResult<Unit> = error("Unexpected proxy call")
    override suspend fun setProxyRouting(serverUrl: String, accessToken: String, mode: ProxyRoutingMode): ApiResult<Unit> = error("Unexpected proxy call")
    var onTestProxyDelay: suspend (String, String, String, Int) -> ApiResult<ProxyDelay> = { _, _, _, _ -> error("Unexpected proxy call") }
    override suspend fun testProxyDelay(serverUrl: String, accessToken: String, group: String, proxy: String, url: String, timeout: Int): ApiResult<ProxyDelay> = onTestProxyDelay(group, proxy, url, timeout)
    var onManagedFrps: suspend () -> ApiResult<ManagedFrps> = { error("Unexpected frps read") }
    var onManagedFrpsEditing: suspend () -> ApiResult<ManagedFrpsEditing> = { error("Unexpected frps secret read") }
    var onSaveManagedFrps: suspend (ManagedFrpsRequest) -> ApiResult<ManagedFrps> = { error("Unexpected frps write") }
    var onStartManagedFrps: suspend () -> ApiResult<TunnelResult> = { error("Unexpected frps start") }
    var onStopManagedFrps: suspend () -> ApiResult<TunnelResult> = { error("Unexpected frps stop") }
    var onManagedFrpsLogs: suspend () -> ApiResult<List<TunnelLog>> = { error("Unexpected frps logs") }
    var onManagedFrpsAudit: suspend () -> ApiResult<List<TunnelAudit>> = { error("Unexpected frps audit") }
    override suspend fun managedFrps(serverUrl: String, accessToken: String) = onManagedFrps()
    override suspend fun managedFrpsEditing(serverUrl: String, accessToken: String) = onManagedFrpsEditing()
    override suspend fun saveManagedFrps(serverUrl: String, accessToken: String, request: ManagedFrpsRequest) = onSaveManagedFrps(request)
    override suspend fun startManagedFrps(serverUrl: String, accessToken: String) = onStartManagedFrps()
    override suspend fun stopManagedFrps(serverUrl: String, accessToken: String) = onStopManagedFrps()
    override suspend fun managedFrpsLogs(serverUrl: String, accessToken: String) = onManagedFrpsLogs()
    override suspend fun managedFrpsAudit(serverUrl: String, accessToken: String) = onManagedFrpsAudit()
    var onTunnelProfiles: suspend () -> ApiResult<List<TunnelProfile>> = { error("Unexpected profiles read") }
    var onTunnelDefinitions: suspend () -> ApiResult<List<TunnelDefinition>> = { error("Unexpected definitions read") }
    var onTunnelRuntime: suspend () -> ApiResult<TunnelRuntime> = { error("Unexpected runtime read") }
    var onTunnelDownload: suspend (String) -> ApiResult<TunnelRuntimeDownload> = { error("Unexpected download read") }
    var onTunnelDetect: suspend (String) -> ApiResult<TunnelRuntime> = { error("Unexpected external detection") }
    var onSaveTunnelProfile: suspend (String?, TunnelProfileRequest) -> ApiResult<TunnelProfile> = { _, _ -> error("Unexpected profile write") }
    var onDeleteTunnelProfile: suspend (String) -> ApiResult<Unit> = { error("Unexpected profile delete") }
    var onTunnelToken: suspend (String, String) -> ApiResult<Unit> = { _, _ -> error("Unexpected token write") }
    var onSaveTunnelDefinition: suspend (String?, TunnelDefinitionRequest) -> ApiResult<TunnelDefinition> = { _, _ -> error("Unexpected definition write") }
    var onDeleteTunnelDefinition: suspend (String) -> ApiResult<Unit> = { error("Unexpected definition delete") }
    var onApplyTunnelProfile: suspend (String) -> ApiResult<TunnelResult> = { error("Unexpected profile apply") }
    var onStopTunnelProfile: suspend (String) -> ApiResult<TunnelResult> = { error("Unexpected profile stop") }
    var onTunnelLogs: suspend (String) -> ApiResult<List<TunnelLog>> = { error("Unexpected logs read") }
    override suspend fun tunnelProfiles(serverUrl: String, accessToken: String) = onTunnelProfiles()
    override suspend fun tunnelDefinitions(serverUrl: String, accessToken: String) = onTunnelDefinitions()
    override suspend fun tunnelRuntime(serverUrl: String, accessToken: String) = onTunnelRuntime()
    override suspend fun tunnelRuntimeDownload(serverUrl: String, accessToken: String, version: String) = onTunnelDownload(version)
    override suspend fun detectTunnelRuntime(serverUrl: String, accessToken: String, path: String) = onTunnelDetect(path)
    override suspend fun saveTunnelProfile(serverUrl: String, accessToken: String, id: String?, request: TunnelProfileRequest) = onSaveTunnelProfile(id, request)
    override suspend fun deleteTunnelProfile(serverUrl: String, accessToken: String, id: String) = onDeleteTunnelProfile(id)
    override suspend fun setTunnelToken(serverUrl: String, accessToken: String, id: String, secret: String) = onTunnelToken(id, secret)
    override suspend fun saveTunnelDefinition(serverUrl: String, accessToken: String, id: String?, request: TunnelDefinitionRequest) = onSaveTunnelDefinition(id, request)
    override suspend fun deleteTunnelDefinition(serverUrl: String, accessToken: String, id: String) = onDeleteTunnelDefinition(id)
    override suspend fun applyTunnelProfile(serverUrl: String, accessToken: String, id: String) = onApplyTunnelProfile(id)
    override suspend fun stopTunnelProfile(serverUrl: String, accessToken: String, id: String) = onStopTunnelProfile(id)
    override suspend fun tunnelLogs(serverUrl: String, accessToken: String, id: String) = onTunnelLogs(id)
    var onKestrelDeployment: suspend (String) -> ApiResult<KestrelCertificateDeployment> = { ApiResult.Transport(null) }
    override suspend fun kestrelCertificateDeployment(serverUrl: String, accessToken: String, id: String) = onKestrelDeployment(id)
    var onCertificates: suspend () -> ApiResult<List<ManagedCertificate>> = { ApiResult.Success(emptyList()) }
    var onCertificate: suspend (String) -> ApiResult<ManagedCertificate> = { ApiResult.Transport(null) }
    var onCertificatePreflight: suspend (List<String>, CertificateChallenge) -> ApiResult<CertificatePreflight> = { _, _ -> ApiResult.Transport(null) }
    var onCertificateMutation: suspend (CertificateAction, String?, JsonBody, String) -> ApiResult<CertificateOperation> = { _, _, _, _ -> ApiResult.Transport(null) }
    var onCertificateOperation: suspend (String) -> ApiResult<CertificateOperation> = { ApiResult.Transport(null) }
    var onCertificateCancel: suspend (String) -> ApiResult<CertificateOperation> = { ApiResult.Transport(null) }
    override suspend fun certificates(serverUrl: String, accessToken: String) = onCertificates()
    override suspend fun certificate(serverUrl: String, accessToken: String, id: String) = onCertificate(id)
    override suspend fun certificatePreflight(serverUrl: String, accessToken: String, domains: List<String>, challenge: CertificateChallenge) = onCertificatePreflight(domains, challenge)
    override suspend fun certificateMutation(serverUrl: String, accessToken: String, action: CertificateAction, id: String?, body: JsonBody, idempotencyKey: String) = onCertificateMutation(action, id, body, idempotencyKey)
    override suspend fun certificateOperation(serverUrl: String, accessToken: String, id: String) = onCertificateOperation(id)
    override suspend fun cancelCertificateOperation(serverUrl: String, accessToken: String, id: String) = onCertificateCancel(id)
    var onWebSites: (suspend (String) -> ApiResult<List<WebServerSite>>)? = null
    override suspend fun webServerSites(serverUrl: String, accessToken: String, instanceId: String): ApiResult<List<WebServerSite>> = requireNotNull(onWebSites)(instanceId)
    var onSaveWebSite: (suspend (String, WebServerSiteRequest) -> ApiResult<WebServerSite>)? = null
    var onDeleteWebSite: (suspend (String, String, String) -> ApiResult<Unit>)? = null
    override suspend fun saveWebServerSite(serverUrl: String, accessToken: String, instanceId: String, request: WebServerSiteRequest): ApiResult<WebServerSite> = requireNotNull(onSaveWebSite)(instanceId, request)
    override suspend fun deleteWebServerSite(serverUrl: String, accessToken: String, instanceId: String, siteId: String, expectedUpdatedAt: String): ApiResult<Unit> = requireNotNull(onDeleteWebSite)(instanceId, siteId, expectedUpdatedAt)
    var onWebDiscover: () -> ApiResult<List<WebServer>> = { ApiResult.Success(emptyList()) }
    var onWebLifecycle: (String, WebServerAction, String) -> ApiResult<WebServerOperation> = { _, _, _ -> ApiResult.Transport(null) }
    var onWebOperation: (String) -> ApiResult<WebServerOperation> = { ApiResult.Transport(null) }
    override suspend fun discoverWebServers(serverUrl: String, accessToken: String): ApiResult<List<WebServer>> = onWebDiscover()
    override suspend fun webServerCandidates(serverUrl: String, accessToken: String): ApiResult<List<WebServerCandidate>> = ApiResult.Success(emptyList())
    override suspend fun webServerInstallCatalog(serverUrl: String, accessToken: String): ApiResult<WebServerInstallCatalog> = ApiResult.Success(WebServerInstallCatalog(null, null, emptyList(), ""))
    override suspend fun integrateWebServer(serverUrl: String, accessToken: String, candidateId: String, confirmed: Boolean, idempotencyKey: String): ApiResult<WebServerOperation> = ApiResult.Transport(null)
    override suspend fun webServerLifecycle(serverUrl: String, accessToken: String, instanceId: String, action: WebServerAction, idempotencyKey: String): ApiResult<WebServerOperation> = onWebLifecycle(instanceId, action, idempotencyKey)
    override suspend fun webServerOperation(serverUrl: String, accessToken: String, operationId: String): ApiResult<WebServerOperation> = onWebOperation(operationId)
    override suspend fun cancelWebServerOperation(serverUrl: String, accessToken: String, operationId: String, idempotencyKey: String): ApiResult<WebServerOperation> = ApiResult.Transport(null)

    var onOutboundProxyStatus: (suspend () -> ApiResult<OutboundProxyStatus>)? = null
    var onSaveOutboundProxy: (suspend (OutboundProxySettings, Boolean) -> ApiResult<OutboundProxyStatus>)? = null
    var onClearOutboundProxy: (suspend () -> ApiResult<OutboundProxyStatus>)? = null
    override suspend fun outboundProxyStatus(serverUrl: String, accessToken: String) = requireNotNull(onOutboundProxyStatus)()
    override suspend fun saveOutboundProxy(serverUrl: String, accessToken: String, settings: OutboundProxySettings, confirmed: Boolean) =
        requireNotNull(onSaveOutboundProxy)(settings, confirmed)
    override suspend fun clearOutboundProxy(serverUrl: String, accessToken: String) = requireNotNull(onClearOutboundProxy)()

    var onStartInstallation: (suspend (InstallationKind, InstallationRequest, String) -> ApiResult<InstallationOperation>)? = null
    var onInstallation: (suspend (String) -> ApiResult<InstallationOperation>)? = null
    var onActiveInstallation: (suspend (InstallationService) -> ApiResult<InstallationOperation?>)? = null
    var onCancelInstallation: (suspend (String, String) -> ApiResult<InstallationOperation>)? = null
    var onInstallationFileReference: (suspend (InstallationService, String) -> ApiResult<InstallationFileReference>)? = null
    var onInstallationUpload: (suspend (InstallationService, String, Long?, () -> InputStream) -> ApiResult<InstallationFileReference>)? = null
    override suspend fun startInstallation(serverUrl: String, accessToken: String, kind: InstallationKind,
        request: InstallationRequest, idempotencyKey: String) = requireNotNull(onStartInstallation)(kind, request, idempotencyKey)
    override suspend fun installation(serverUrl: String, accessToken: String, operationId: String) = requireNotNull(onInstallation)(operationId)
    override suspend fun activeInstallation(serverUrl: String, accessToken: String, service: InstallationService) = requireNotNull(onActiveInstallation)(service)
    override suspend fun cancelInstallation(serverUrl: String, accessToken: String, operationId: String,
        idempotencyKey: String) = requireNotNull(onCancelInstallation)(operationId, idempotencyKey)
    override suspend fun installationFileReference(serverUrl: String, accessToken: String, service: InstallationService,
        path: String) = requireNotNull(onInstallationFileReference)(service, path)
    override suspend fun uploadInstallationPackage(serverUrl: String, accessToken: String, service: InstallationService,
        fileName: String, length: Long?, open: () -> InputStream, onProgress: ((Long) -> Unit)?) =
        requireNotNull(onInstallationUpload)(service, fileName, length, open)

    var onDeploymentApplications: (suspend (String, String) -> ApiResult<List<DeploymentApplication>>)? = null
    var onDeploymentSnapshot: (suspend (String, String, String) -> ApiResult<DeploymentSnapshot>)? = null
    var onUpdateDeploymentDefinition: suspend (String, DeploymentDefinitionUpdate, String) -> ApiResult<DeploymentApplication> = { _, _, _ -> ApiResult.Transport(null) }
    override suspend fun updateDeploymentDefinition(serverUrl: String, accessToken: String, applicationId: String, definition: DeploymentDefinitionUpdate, idempotencyKey: String) =
        onUpdateDeploymentDefinition(applicationId, definition, idempotencyKey)
    var onDeploymentRuntime: (suspend (String, String) -> ApiResult<DeploymentRuntime>)? = null
    var onDeploymentTemplates: (suspend (String, String) -> ApiResult<List<DeploymentTemplate>>)? = null
    var onDeploymentLogs: (suspend (String, String, String, Int) -> ApiResult<DeploymentLog>)? = null
    var onRollbackDeployment: (suspend (String, String, String, String, String) -> ApiResult<DeploymentOperation>)? = null

    override suspend fun deploymentApplications(serverUrl: String, accessToken: String) =
        requireNotNull(onDeploymentApplications)(serverUrl, accessToken)
    override suspend fun deploymentSnapshot(serverUrl: String, accessToken: String, applicationId: String) =
        requireNotNull(onDeploymentSnapshot)(serverUrl, accessToken, applicationId)
    override suspend fun deploymentRuntime(serverUrl: String, accessToken: String) =
        requireNotNull(onDeploymentRuntime)(serverUrl, accessToken)
    override suspend fun deploymentTemplates(serverUrl: String, accessToken: String) =
        requireNotNull(onDeploymentTemplates)(serverUrl, accessToken)
    override suspend fun deploymentLogs(serverUrl: String, accessToken: String, applicationId: String, tail: Int) =
        requireNotNull(onDeploymentLogs)(serverUrl, accessToken, applicationId, tail)
    override suspend fun rollbackDeployment(serverUrl: String, accessToken: String, applicationId: String, revisionId: String, idempotencyKey: String) =
        requireNotNull(onRollbackDeployment)(serverUrl, accessToken, applicationId, revisionId, idempotencyKey)

    var onLogin: (suspend (String, String, CharArray) -> ApiResult<LoginSession>)? = null
    var onHostOperatingSystem: (suspend (String) -> ApiResult<HostOperatingSystemKind>)? = null
    var onRefresh: (suspend (String, String) -> ApiResult<AuthTokens>)? = null
    var onLogout: (suspend (String, String, String) -> ApiResult<Unit>)? = null
    var onElevation: (suspend (String, String, String, String, CharArray?, String?) -> ApiResult<ElevationGrant>)? = null
    var onFileElevation: (suspend (String, String, String, String?, CharArray?, List<String>, Boolean, String?) -> ApiResult<FileElevationGrant>)? = null
    var onListDirectory: (suspend (String, String, String) -> ApiResult<DirectoryListing>)? = null
    var onSetFilePermissions: (suspend (String, String, String, Int) -> ApiResult<RemoteFileProperties>)? = null
    var onFileProperties: (suspend (String, String, String) -> ApiResult<RemoteFileProperties>)? = null
    var onCreateDirectory: (suspend (String, String, String) -> ApiResult<Unit>)? = null
    var onDelete: (suspend (String, String, String) -> ApiResult<Unit>)? = null
    var onRename: (suspend (String, String, String, String) -> ApiResult<Unit>)? = null
    var onMove: (suspend (String, String, String, String) -> ApiResult<Unit>)? = null
    var onCopy: (suspend (String, String, String, String) -> ApiResult<Unit>)? = null
    var onUpload: (suspend (String, String, String, String, InputStream, Long?, ((Long) -> Unit)?) -> ApiResult<Unit>)? = null
    var onPerformanceInfo: (suspend (String, String) -> ApiResult<PerformanceInfo>)? = null
    var onPerformanceHistory: (suspend (String, String) -> ApiResult<List<PerformanceSnapshot>>)? = null
    var onNetworkAddresses: (suspend (String, String) -> ApiResult<List<NetworkAddress>>)? = null
    var onPerformance: (suspend (String, String) -> ApiResult<PerformanceSnapshot>)? = null
    var onProcesses: (suspend (String, String, Int, Int, String?, ProcessSort, Boolean) -> ApiResult<ProcessPage>)? = null
    var onKill: (suspend (String, String, Int, String) -> ApiResult<ProcessKillResult>)? = null
    var onDownload: (suspend (String, String, String, DownloadSink, ((Long, Long?) -> Unit)?) -> ApiResult<Long>)? = null
    var onThumbnail: (suspend (String, String, String, Int) -> ApiResult<ByteArray>)? = null
    var onCreateUploadSession:
        (suspend (String, String, String, String, Long, Long?, String) -> ApiResult<UploadSession>)? = null
    var onUploadSession: (suspend (String, String, String) -> ApiResult<UploadSession>)? = null
    var onSendUploadChunk:
        (suspend (String, String, String, Long, Long, InputStream, ((Long) -> Unit)?) -> UploadChunkResult)? = null
    var onCommitUpload: (suspend (String, String, String, String?) -> ApiResult<Unit>)? = null
    var onAbortUpload: (suspend (String, String, String) -> ApiResult<Unit>)? = null

    var loginCount = 0
        private set

    var refreshCount = 0
        private set

    var elevationCount = 0
        private set

    var logoutCount = 0
        private set

    val elevationTargets = mutableListOf<String>()
    val elevationAccounts = mutableListOf<String?>()
    val listDirectoryPaths = mutableListOf<String>()
    val killCalls = mutableListOf<Pair<Int, String>>()

    /** Abandoned sessions, so a test can assert that cancel really told the server. */
    val abortedUploadIds = mutableListOf<String>()
    val committedUploadIds = mutableListOf<String>()

    /** Offsets requested through the authoritative read, so a test can count resynchronisations. */
    val uploadSessionReads = mutableListOf<String>()

    override suspend fun login(serverUrl: String, identifier: String, password: CharArray): ApiResult<LoginSession> {
        loginCount++
        return requireHandler(onLogin, "login")(serverUrl, identifier, password)
    }

    /** Addresses asked for their host operating system, so a test can prove who was *not* asked. */
    val hostOperatingSystemLookups = mutableListOf<String>()

    override suspend fun hostOperatingSystem(serverUrl: String): ApiResult<HostOperatingSystemKind> {
        hostOperatingSystemLookups += serverUrl
        return requireHandler(onHostOperatingSystem, "hostOperatingSystem")(serverUrl)
    }

    override suspend fun refresh(serverUrl: String, refreshToken: String): ApiResult<AuthTokens> {
        refreshCount++
        return requireHandler(onRefresh, "refresh")(serverUrl, refreshToken)
    }

    override suspend fun logout(serverUrl: String, accessToken: String, refreshToken: String): ApiResult<Unit> {
        logoutCount++
        return requireHandler(onLogout, "logout")(serverUrl, accessToken, refreshToken)
    }

    override suspend fun requestElevation(
        serverUrl: String,
        accessToken: String,
        capability: String,
        target: String,
        password: CharArray?,
        administratorUsername: String?,
    ): ApiResult<ElevationGrant> {
        elevationCount++
        elevationTargets += target
        elevationAccounts += administratorUsername
        return requireHandler(onElevation, "requestElevation")(serverUrl, accessToken, capability, target, password, administratorUsername)
    }

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
        elevationCount++
        elevationTargets += path
        elevationAccounts += administratorUsername
        return requireHandler(onFileElevation, "requestFileElevation")(
            serverUrl,
            accessToken,
            path,
            capability,
            password,
            relatedPaths,
            includeDescendants,
            administratorUsername,
        )
    }

    override suspend fun listDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<DirectoryListing> {
        listDirectoryPaths += path
        return requireHandler(onListDirectory, "listDirectory")(serverUrl, accessToken, path)
    }

    override suspend fun setFilePermissions(serverUrl: String, accessToken: String, path: String, unixMode: Int): ApiResult<RemoteFileProperties> =
        requireHandler(onSetFilePermissions, "setFilePermissions")(serverUrl, accessToken, path, unixMode)

    var onTerminalSettings: (suspend (String) -> ApiResult<app.relaxkonos.mobile.core.net.TerminalSettings>)? = null
    var onSaveTerminalSettings: (suspend (String, app.relaxkonos.mobile.core.net.TerminalSettings) -> ApiResult<app.relaxkonos.mobile.core.net.TerminalSettings>)? = null
    override suspend fun terminalSettings(serverUrl: String, accessToken: String, workspaceId: String) = requireNotNull(onTerminalSettings)(workspaceId)
    override suspend fun saveTerminalSettings(serverUrl: String, accessToken: String, workspaceId: String, settings: app.relaxkonos.mobile.core.net.TerminalSettings) = requireNotNull(onSaveTerminalSettings)(workspaceId, settings)

    override suspend fun fileProperties(serverUrl: String, accessToken: String, path: String): ApiResult<RemoteFileProperties> =
        requireHandler(onFileProperties, "fileProperties")(serverUrl, accessToken, path)

    override suspend fun createDirectory(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> =
        requireHandler(onCreateDirectory, "createDirectory")(serverUrl, accessToken, path)

    override suspend fun delete(serverUrl: String, accessToken: String, path: String): ApiResult<Unit> =
        requireHandler(onDelete, "delete")(serverUrl, accessToken, path)

    override suspend fun rename(serverUrl: String, accessToken: String, sourcePath: String, newName: String): ApiResult<Unit> =
        requireHandler(onRename, "rename")(serverUrl, accessToken, sourcePath, newName)

    override suspend fun move(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit> =
        requireHandler(onMove, "move")(serverUrl, accessToken, sourcePath, destinationPath)

    override suspend fun copy(serverUrl: String, accessToken: String, sourcePath: String, destinationPath: String): ApiResult<Unit> =
        requireHandler(onCopy, "copy")(serverUrl, accessToken, sourcePath, destinationPath)

    override suspend fun upload(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        source: InputStream,
        contentLength: Long?,
        onProgress: ((Long) -> Unit)?,
    ): ApiResult<Unit> = requireHandler(onUpload, "upload")(
        serverUrl,
        accessToken,
        targetDirectoryPath,
        fileName,
        source,
        contentLength,
        onProgress,
    )

    override suspend fun createUploadSession(
        serverUrl: String,
        accessToken: String,
        targetDirectoryPath: String,
        fileName: String,
        length: Long,
        lastModifiedMillis: Long?,
        idempotencyKey: String,
    ): ApiResult<UploadSession> = requireHandler(onCreateUploadSession, "createUploadSession")(
        serverUrl,
        accessToken,
        targetDirectoryPath,
        fileName,
        length,
        lastModifiedMillis,
        idempotencyKey,
    )

    override suspend fun uploadSession(serverUrl: String, accessToken: String, uploadId: String): ApiResult<UploadSession> {
        uploadSessionReads += uploadId
        return requireHandler(onUploadSession, "uploadSession")(serverUrl, accessToken, uploadId)
    }

    override suspend fun sendUploadChunk(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        offset: Long,
        chunkLength: Long,
        source: InputStream,
        onInFlight: ((Long) -> Unit)?,
    ): UploadChunkResult = requireHandler(onSendUploadChunk, "sendUploadChunk")(
        serverUrl,
        accessToken,
        uploadId,
        offset,
        chunkLength,
        source,
        onInFlight,
    )

    override suspend fun commitUpload(
        serverUrl: String,
        accessToken: String,
        uploadId: String,
        contentHash: String?,
    ): ApiResult<Unit> {
        committedUploadIds += uploadId
        return requireHandler(onCommitUpload, "commitUpload")(serverUrl, accessToken, uploadId, contentHash)
    }

    override suspend fun abortUpload(serverUrl: String, accessToken: String, uploadId: String): ApiResult<Unit> {
        abortedUploadIds += uploadId
        return requireHandler(onAbortUpload, "abortUpload")(serverUrl, accessToken, uploadId)
    }

    override suspend fun performanceInfo(serverUrl: String, accessToken: String): ApiResult<PerformanceInfo> =
        requireHandler(onPerformanceInfo, "performanceInfo")(serverUrl, accessToken)
    override suspend fun performanceHistory(serverUrl: String, accessToken: String): ApiResult<List<PerformanceSnapshot>> =
        requireHandler(onPerformanceHistory, "performanceHistory")(serverUrl, accessToken)
    override suspend fun networkAddresses(serverUrl: String, accessToken: String): ApiResult<List<NetworkAddress>> =
        requireHandler(onNetworkAddresses, "networkAddresses")(serverUrl, accessToken)
    override suspend fun performanceSnapshot(serverUrl: String, accessToken: String): ApiResult<PerformanceSnapshot> =
        requireHandler(onPerformance, "performanceSnapshot")(serverUrl, accessToken)
    override suspend fun queryProcesses(
        serverUrl: String,
        accessToken: String,
        page: Int,
        pageSize: Int,
        filter: String?,
        sort: ProcessSort,
        descending: Boolean,
    ): ApiResult<ProcessPage> = requireHandler(onProcesses, "queryProcesses")(serverUrl, accessToken, page, pageSize, filter, sort, descending)

    override suspend fun killProcess(serverUrl: String, accessToken: String, pid: Int, expectedStartTime: String): ApiResult<ProcessKillResult> {
        killCalls += pid to expectedStartTime
        return requireHandler(onKill, "killProcess")(serverUrl, accessToken, pid, expectedStartTime)
    }

    override suspend fun download(
        serverUrl: String,
        accessToken: String,
        path: String,
        sink: DownloadSink,
        onProgress: ((Long, Long?) -> Unit)?,
    ): ApiResult<Long> = requireHandler(onDownload, "download")(serverUrl, accessToken, path, sink, onProgress)

    override suspend fun thumbnail(
        serverUrl: String,
        accessToken: String,
        path: String,
        maxEdge: Int,
    ): ApiResult<ByteArray> = requireHandler(onThumbnail, "thumbnail")(serverUrl, accessToken, path, maxEdge)

    private fun <T> requireHandler(handler: T?, name: String): T =
        handler ?: error("FakeGateway.$name was called but no handler was configured.")
}

/** A successful login result with the given token values. */
fun loginSession(
    accessToken: String = "access-1",
    refreshToken: String = "refresh-1",
    userName: String = "nana",
    workspaceName: String = "studio",
    capabilities: Set<String> = emptySet(),
): LoginSession = LoginSession(
    userName = userName,
    workspaceName = workspaceName,
    server = ServerDescriptor(platform = "linux", capabilities = capabilities),
    tokens = AuthTokens(
        accessToken = accessToken,
        refreshToken = refreshToken,
        accessTokenExpiresAtMillis = null,
        refreshTokenExpiresAtMillis = null,
    ),
 workspaceId = "11111111-1111-1111-1111-111111111111")
