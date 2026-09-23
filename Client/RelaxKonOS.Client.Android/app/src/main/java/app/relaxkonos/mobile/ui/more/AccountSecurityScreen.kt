package app.relaxkonos.mobile.ui.more

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.security.BiometricCapability
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.VaultRecordState
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.IconBadge
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.ListRow
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.formatTimestamp
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Spacing

/**
 * Account and security.
 *
 * This is the only page that can destroy stored credentials, and it is the only place the fingerprint
 * master switch lives. Turning the switch off clears both vaults *and* both Keystore keys, because a
 * ciphertext nobody can decrypt is still a liability sitting in the app's private storage. The design
 * forbids merging the two vaults, so they are listed and cleared as two separate domains throughout
 * (`RelaxKonOS.Mobile.V1.Design.md` §5.2).
 */
@Composable
fun AccountSecurityScreen(
    onBack: (() -> Unit)?,
    modifier: Modifier = Modifier,
) {
    val container = appContainer()
    val appearance = container.appearance

    var revision by remember { mutableStateOf(0) }
    var confirmDisable by remember { mutableStateOf(false) }
    var confirmClearAll by remember { mutableStateOf(false) }
    var deleteTarget by remember { mutableStateOf<DeletionTarget?>(null) }

    val capability = container.biometricCapability()
    val connectionRecords = remember(revision, appearance.fingerprintEnabled) { container.vault.records(VaultKind.Connection) }
    val elevationRecords = remember(revision, appearance.fingerprintEnabled) { container.vault.records(VaultKind.Elevation) }
    // A debug build may hold one plaintext record in the case where the vault cannot exist at all. It
    // is listed here and nowhere else, because this page is the one place that answers "what is stored
    // on this device" — a plaintext password it does not mention would be the one dishonest thing on
    // the screen. `null` in every release build.
    val debugRecord = remember(revision) { container.debugCredentials?.record() }
    val connectionMode = container.unlockMode(VaultKind.Connection)
    val elevationMode = container.unlockMode(VaultKind.Elevation)

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.account_security_title),
            onBack = onBack,
        )

        SectionCard(
            title = stringResource(R.string.account_security_fingerprint),
            contentSpacing = Spacing.xs,
        ) {
            ListRow(
                title = stringResource(R.string.account_security_fingerprint_subtitle),
                trailing = {
                    Switch(
                        checked = appearance.fingerprintEnabled,
                        onCheckedChange = { enabled ->
                            if (enabled) {
                                appearance.setFingerprintEnabled(true)
                            } else {
                                confirmDisable = true
                            }
                        },
                    )
                },
                onClick = {
                    if (appearance.fingerprintEnabled) {
                        confirmDisable = true
                    } else {
                        appearance.setFingerprintEnabled(true)
                    }
                },
            )
        }

        SectionCard(stringResource(R.string.account_security_device_title)) {
            KeyValueRow(stringResource(R.string.account_security_device_strength), capabilityName(capability))
            KeyValueRow(
                stringResource(R.string.account_security_connection_vault),
                stringResource(
                    when (connectionMode) {
                        null -> R.string.account_security_vault_disabled
                        VaultUnlockMode.PerUseStrongBiometric -> R.string.account_security_vault_mode_strong
                        VaultUnlockMode.DeviceUnlockWindow -> R.string.account_security_vault_mode_window
                    },
                ),
            )
            KeyValueRow(
                stringResource(R.string.account_security_elevation_vault),
                stringResource(
                    when (elevationMode) {
                        null -> R.string.account_security_elevation_unavailable
                        VaultUnlockMode.PerUseStrongBiometric -> R.string.account_security_vault_mode_strong
                        VaultUnlockMode.DeviceUnlockWindow -> R.string.account_security_vault_mode_window
                    },
                ),
            )
            if (capability.isReducedStrength) {
                Text(
                    stringResource(R.string.account_security_reduced_strength),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error,
                )
            }
        }

        SectionCard(stringResource(R.string.account_security_connection_vault)) {
            if (connectionRecords.isEmpty() && debugRecord == null) {
                EmptyHint(stringResource(R.string.account_security_connection_vault_empty))
            } else {
                connectionRecords.forEach { record ->
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                    ) {
                        IconBadge(icon = DesktopIcons.connections)
                        Column(Modifier.weight(1f)) {
                            Text(record.serverUrl, style = MaterialTheme.typography.bodyMedium)
                            Text(
                                listOfNotNull(record.account, formatTimestamp(record.lastUsedEpochMillis)).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            InvalidatedNote(record)
                        }
                        TextButton(onClick = { deleteTarget = DeletionTarget.Vault(record) }) {
                            Text(stringResource(R.string.common_delete))
                        }
                    }
                }
                debugRecord?.let { record ->
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                    ) {
                        IconBadge(icon = DesktopIcons.connections)
                        Column(Modifier.weight(1f)) {
                            Text(record.serverUrl, style = MaterialTheme.typography.bodyMedium)
                            Text(
                                record.identifier,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            Text(
                                stringResource(R.string.account_security_debug_record_note),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.error,
                            )
                        }
                        TextButton(onClick = { deleteTarget = DeletionTarget.DebugStore }) {
                            Text(stringResource(R.string.common_delete))
                        }
                    }
                }
            }
        }

        SectionCard(stringResource(R.string.account_security_elevation_vault)) {
            if (elevationRecords.isEmpty()) {
                EmptyHint(stringResource(R.string.account_security_elevation_vault_empty))
            } else {
                elevationRecords.forEach { record ->
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(Spacing.sm),
                    ) {
                        IconBadge(icon = DesktopIcons.host)
                        Column(Modifier.weight(1f)) {
                            Text(record.account, style = MaterialTheme.typography.bodyMedium)
                            Text(
                                listOfNotNull(record.serverUrl, formatTimestamp(record.lastUsedEpochMillis)).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            InvalidatedNote(record)
                        }
                        TextButton(onClick = { deleteTarget = DeletionTarget.Vault(record) }) {
                            Text(stringResource(R.string.common_delete))
                        }
                    }
                }
            }
            Text(
                stringResource(R.string.account_security_elevation_note),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }

        TextButton(
            onClick = { confirmClearAll = true },
            enabled = connectionRecords.isNotEmpty() || elevationRecords.isNotEmpty() || debugRecord != null,
        ) { Text(stringResource(R.string.account_security_clear_all)) }
    }

    if (confirmDisable) {
        ConfirmDangerousDialog(
            title = stringResource(R.string.account_security_disable_title),
            message = stringResource(R.string.account_security_disable_message),
            confirmLabel = stringResource(R.string.account_security_disable_confirm),
            onConfirm = {
                confirmDisable = false
                appearance.setFingerprintEnabled(false)
                container.vault.clear(VaultKind.Connection)
                container.vault.clear(VaultKind.Elevation)
                // The switch means "no password is stored at all", and a plaintext file is the least
                // defensible thing to leave behind when the user has just asked for nothing to be kept.
                container.debugCredentials?.clear()
                container.keyManager.deleteKey(VaultKind.Connection)
                container.keyManager.deleteKey(VaultKind.Elevation)
                revision++
            },
            onDismiss = { confirmDisable = false },
        )
    }

    if (confirmClearAll) {
        ConfirmDangerousDialog(
            title = stringResource(R.string.account_security_clear_all_title),
            message = stringResource(R.string.account_security_clear_all_message),
            confirmLabel = stringResource(R.string.account_security_clear_all),
            onConfirm = {
                confirmClearAll = false
                container.vault.clear(VaultKind.Connection)
                container.vault.clear(VaultKind.Elevation)
                container.debugCredentials?.clear()
                revision++
            },
            onDismiss = { confirmClearAll = false },
        )
    }

    deleteTarget?.let { target ->
        // Both cases are "the saved password for this identity on this server", so they share the
        // sentence; only what is removed differs.
        val (identifier, serverUrl, remove) = when (target) {
            is DeletionTarget.Vault -> Triple(
                target.record.account,
                target.record.serverUrl,
                { container.vault.delete(target.record) },
            )

            DeletionTarget.DebugStore -> Triple(
                debugRecord?.identifier.orEmpty(),
                debugRecord?.serverUrl.orEmpty(),
                { container.debugCredentials?.clear() },
            )
        }
        ConfirmDangerousDialog(
            title = stringResource(R.string.account_security_delete_title),
            message = stringResource(R.string.account_security_delete_message, identifier, serverUrl),
            confirmLabel = stringResource(R.string.common_delete),
            onConfirm = {
                deleteTarget = null
                remove()
                revision++
            },
            onDismiss = { deleteTarget = null },
        )
    }
}

/**
 * What a delete confirmation is about to remove.
 *
 * A debug build can hold one credential outside the vault, and the confirm dialog has to be able to
 * name it without pretending it is a vault record.
 */
private sealed interface DeletionTarget {
    data class Vault(val record: VaultRecord) : DeletionTarget

    data object DebugStore : DeletionTarget
}

/**
 * Says why a stored record can never be unsealed again.
 *
 * A record in this state stays listed on purpose: removing it would erase the only trace of what
 * happened to the saved password, and the user would go on saving it and losing it without ever seeing
 * a reason (`RelaxKonOS.Mobile.LoginCredentials.Design.md` §7.4).
 */
@Composable
private fun InvalidatedNote(record: VaultRecord) {
    if (record.state != VaultRecordState.Invalidated) {
        return
    }
    Text(
        stringResource(R.string.account_security_record_invalidated),
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.error,
    )
}

@Composable
private fun capabilityName(capability: BiometricCapability): String = stringResource(
    when (capability) {
        BiometricCapability.Strong -> R.string.biometric_strength_strong
        BiometricCapability.WeakOnly -> R.string.biometric_strength_weak
        BiometricCapability.DeviceCredentialOnly -> R.string.biometric_strength_device_credential
        BiometricCapability.None -> R.string.biometric_strength_none
    },
)
