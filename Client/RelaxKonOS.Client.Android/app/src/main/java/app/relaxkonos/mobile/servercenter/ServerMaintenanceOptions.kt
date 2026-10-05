package app.relaxkonos.mobile.servercenter

/** Certificate rotation is explicit and scoped to a system installation repair. */
internal fun maintenanceOptions(
    kind: ServerDeploymentKind,
    mode: ServerInstallMode,
    installationId: String,
    purge: Boolean = false,
    repairCertificate: Boolean = false,
    certificateIdentities: String = "",
    addFirewallRule: Boolean = false,
    removeComponents: String = "",
): ServerDeploymentOptions {
    require(ServerInstallationId.isValid(installationId))
    val rotate = kind == ServerDeploymentKind.Repair && repairCertificate
    val identities = if (rotate) {
        require(mode != ServerInstallMode.LinuxUser) { "server-deployment.not_supported" }
        normalizeRepairCertificateIdentities(certificateIdentities)
    } else null
    return ServerDeploymentOptions(
        ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, mode = mode,
        expectedInstallationId = installationId, confirmed = true,
        removeComponents = if (kind == ServerDeploymentKind.Uninstall && mode != ServerInstallMode.LinuxUser) removeComponents else "",
        retention = if (kind == ServerDeploymentKind.Uninstall && purge) ServerDataRetention.Delete else ServerDataRetention.Retain,
        certificateMode = if (rotate) "selfSigned" else null,
        selfSignedIdentities = identities,
        addFirewallRule = kind == ServerDeploymentKind.Repair && mode != ServerInstallMode.LinuxUser && addFirewallRule,
    )
}

internal fun normalizeRepairCertificateIdentities(value: String): String {
    require(value.length in 1..4096) { "server-deployment.invalid_request" }
    val identities = value.split(',').map(String::trim)
    require(identities.all { it.isNotEmpty() && (it.matches(Regex("[A-Za-z0-9][A-Za-z0-9.-]*")) ||
        it.contains(':') && it.matches(Regex("[0-9A-Fa-f:]+"))) }) { "server-deployment.invalid_request" }
    return identities.distinct().joinToString(",")
}
