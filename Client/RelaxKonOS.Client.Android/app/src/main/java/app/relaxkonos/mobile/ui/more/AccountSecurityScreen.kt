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
import androidx.compose.ui.unit.dp
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.security.BiometricCapability
import app.relaxkonos.mobile.security.VaultKind
import app.relaxkonos.mobile.security.VaultRecord
import app.relaxkonos.mobile.security.VaultUnlockMode
import app.relaxkonos.mobile.ui.common.ConfirmDangerousDialog
import app.relaxkonos.mobile.ui.common.EmptyHint
import app.relaxkonos.mobile.ui.common.KeyValueRow
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.appContainer
import app.relaxkonos.mobile.ui.common.formatTimestamp

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
    var deleteTarget by remember { mutableStateOf<VaultRecord?>(null) }

    val capability = container.biometricCapability()
    val connectionRecords = remember(revision, appearance.fingerprintEnabled) { container.vault.records(VaultKind.Connection) }
    val elevationRecords = remember(revision, appearance.fingerprintEnabled) { container.vault.records(VaultKind.Elevation) }
    val connectionMode = container.unlockMode(VaultKind.Connection)
    val elevationMode = container.unlockMode(VaultKind.Elevation)

    Column(
        modifier = modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (onBack != null) {
            TextButton(onClick = onBack) { Text(stringResource(R.string.common_back)) }
        }
        Text(stringResource(R.string.account_security_title), style = MaterialTheme.typography.headlineSmall)

        SectionCard(stringResource(R.string.account_security_fingerprint)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
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
                Text(
                    stringResource(R.string.account_security_fingerprint_subtitle),
                    modifier = Modifier.padding(start = 12.dp),
                    style = MaterialTheme.typography.bodyMedium,
                )
            }
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
            if (connectionRecords.isEmpty()) {
                EmptyHint(stringResource(R.string.account_security_connection_vault_empty))
            } else {
                connectionRecords.forEach { record ->
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(record.serverUrl, style = MaterialTheme.typography.bodyMedium)
                            Text(
                                listOfNotNull(record.account, formatTimestamp(record.lastUsedEpochMillis)).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        TextButton(onClick = { deleteTarget = record }) { Text(stringResource(R.string.common_delete)) }
                    }
                }
            }
        }

        SectionCard(stringResource(R.string.account_security_elevation_vault)) {
            if (elevationRecords.isEmpty()) {
                EmptyHint(stringResource(R.string.account_security_elevation_vault_empty))
            } else {
                elevationRecords.forEach { record ->
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text(record.account, style = MaterialTheme.typography.bodyMedium)
                            Text(
                                listOfNotNull(record.serverUrl, formatTimestamp(record.lastUsedEpochMillis)).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        TextButton(onClick = { deleteTarget = record }) { Text(stringResource(R.string.common_delete)) }
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
            enabled = connectionRecords.isNotEmpty() || elevationRecords.isNotEmpty(),
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
                revision++
            },
            onDismiss = { confirmClearAll = false },
        )
    }

    deleteTarget?.let { record ->
        ConfirmDangerousDialog(
            title = stringResource(R.string.account_security_delete_title),
            message = stringResource(R.string.account_security_delete_message, record.account, record.serverUrl),
            confirmLabel = stringResource(R.string.common_delete),
            onConfirm = {
                deleteTarget = null
                container.vault.delete(record)
                revision++
            },
            onDismiss = { deleteTarget = null },
        )
    }
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
