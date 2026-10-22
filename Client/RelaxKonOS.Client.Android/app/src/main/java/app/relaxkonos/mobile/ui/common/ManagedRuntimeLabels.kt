package app.relaxkonos.mobile.ui.common

import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R

@Composable internal fun managedRuntimeOwnershipLabel(value: String) = stringResource(when (value) {
    "absent" -> R.string.mihomo_state_notinstalled
    "external" -> R.string.managed_runtime_ownership_external
    "managed" -> R.string.managed_runtime_ownership_managed
    else -> R.string.managed_resource_unknown
})
@Composable internal fun managedRuntimeTrustLabel(value: String) = stringResource(when (value) {
    "unconfirmed" -> R.string.managed_runtime_trust_unconfirmed
    "confirmed" -> R.string.managed_runtime_trust_confirmed
    "revoked" -> R.string.managed_runtime_trust_revoked
    "conflict" -> R.string.managed_runtime_trust_conflict
    else -> R.string.managed_resource_unknown
})
@Composable internal fun managedRuntimeEvidenceLabel(value: String) = stringResource(when (value) {
    "nativeConfiguration" -> R.string.managed_runtime_evidence_native
    "interoperability" -> R.string.managed_runtime_evidence_interoperability
    else -> R.string.managed_runtime_evidence_unverified
})
@Composable internal fun managedRuntimeDependencyKindLabel(value: String) = stringResource(when (value) {
    "platform" -> R.string.managed_runtime_dependency_platform
    "serviceHost" -> R.string.managed_runtime_dependency_host
    "diskSpace" -> R.string.managed_runtime_dependency_disk
    "fileSystem" -> R.string.managed_runtime_dependency_filesystem
    "kernelFeature" -> R.string.managed_runtime_dependency_kernel
    else -> R.string.managed_resource_unknown
})
@Composable internal fun managedRuntimeDependencyStateLabel(value: String) = stringResource(when (value) {
    "satisfied" -> R.string.managed_runtime_dependency_satisfied
    "missing" -> R.string.managed_resource_missing
    else -> R.string.managed_resource_unknown
})
