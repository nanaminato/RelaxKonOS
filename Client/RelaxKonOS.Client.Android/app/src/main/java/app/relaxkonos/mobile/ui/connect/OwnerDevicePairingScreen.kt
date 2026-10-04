package app.relaxkonos.mobile.ui.connect

import app.relaxkonos.mobile.ui.common.rememberUsageOpenDocument

import app.relaxkonos.mobile.ui.common.ActionFeedback
import app.relaxkonos.mobile.ui.common.OperationMessageDialog
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.viewmodel.compose.viewModel
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.auth.OwnerDevicePairingInvitation
import app.relaxkonos.mobile.ui.common.ScreenHeader
import app.relaxkonos.mobile.ui.theme.Spacing
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import com.google.mlkit.vision.common.InputImage
import java.text.DateFormat
import java.util.Date

/**
 * The one Android entry point for joining a Windows 10/11 owner-device domain.
 *
 * Camera and gallery recognition both only fill the manual text field. The user still sees the
 * parsed HTTPS server and invitation expiry and must explicitly confirm before key generation or
 * network enrollment starts.
 */
@Composable
fun OwnerDevicePairingScreen(modifier: Modifier = Modifier, onClose: () -> Unit) {
    val activity = LocalContext.current as? FragmentActivity ?: return
    val context = LocalContext.current
    val viewModel: LoginViewModel = viewModel()
    var scanMessage by remember { mutableStateOf<Int?>(null) }
    var invitation by remember { mutableStateOf<OwnerDevicePairingInvitation?>(null) }
    var scanningImage by remember { mutableStateOf(false) }
    val options = remember {
        BarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
    }

    fun useScannedValue(value: String?) {
        if (value.isNullOrBlank()) {
            scanMessage = R.string.owner_device_scan_no_code
        } else {
            viewModel.changeOwnerDevicePairingCode(value)
            scanMessage = null
        }
    }

    fun scanImage(uri: Uri) {
        scanningImage = true
        scanMessage = null
        val scanner = BarcodeScanning.getClient(options)
        runCatching { InputImage.fromFilePath(context, uri) }
            .onSuccess { image ->
                scanner.process(image)
                    .addOnSuccessListener { barcodes ->
                        scanner.close()
                        scanningImage = false
                        useScannedValue(barcodes.firstOrNull { !it.rawValue.isNullOrBlank() }?.rawValue)
                    }
                    .addOnFailureListener {
                        scanner.close()
                        scanningImage = false
                        scanMessage = R.string.owner_device_scan_failed
                    }
            }
            .onFailure {
                scanner.close()
                scanningImage = false
                scanMessage = R.string.owner_device_scan_failed
            }
    }

    val imagePicker = rememberLauncherForActivityResult(rememberUsageOpenDocument("OwnerDevicePairingScreen.pairing-image")) { uri ->
        if (uri != null) scanImage(uri)
    }
    val codeScanner = remember(activity) {
        GmsBarcodeScanning.getClient(
            activity,
            GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build(),
        )
    }

    Column(
        modifier = modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.owner_device_add_title),
            subtitle = stringResource(R.string.owner_device_add_subtitle),
            onBack = onClose,
        )

        // This flow's own failures belong on the screen that produced them. It shares its view model
        // with the login screen, so without this every pairing failure was reported as a dialog on the
        // login screen — shown only after the user had navigated away from where it happened.
        viewModel.message?.let { banner ->
            ActionFeedback(message = banner, onRetry = null, onDismiss = { viewModel.dismissMessage() })
        }

        OutlinedTextField(
            value = viewModel.ownerDevicePairingCode,
            onValueChange = {
                viewModel.changeOwnerDevicePairingCode(it)
                scanMessage = null
            },
            label = { Text(stringResource(R.string.owner_device_pairing_code)) },
            modifier = Modifier.fillMaxWidth(),
            minLines = 4,
            enabled = !viewModel.isLoggingIn && !scanningImage,
            shape = MaterialTheme.shapes.medium,
        )

        OutlinedButton(
            onClick = {
                scanMessage = null
                codeScanner.startScan()
                    .addOnSuccessListener { barcode -> useScannedValue(barcode.rawValue) }
                    .addOnFailureListener { scanMessage = R.string.owner_device_scan_failed }
            },
            enabled = !viewModel.isLoggingIn && !scanningImage,
            modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(R.string.owner_device_scan_camera)) }

        OutlinedButton(
            onClick = { imagePicker.launch(arrayOf("image/*")) },
            enabled = !viewModel.isLoggingIn && !scanningImage,
            modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(R.string.owner_device_scan_image)) }

        OperationMessageDialog(scanMessage?.let { stringResource(it) }, onDismiss = { scanMessage = null })

        Button(
            onClick = {
                invitation = runCatching { OwnerDevicePairingInvitation.parse(viewModel.ownerDevicePairingCode) }
                    .getOrElse {
                        scanMessage = R.string.owner_device_pairing_invalid
                        null
                    }
            },
            enabled = !viewModel.isLoggingIn && !scanningImage,
            modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(R.string.owner_device_pair_next)) }
    }

    invitation?.let { pending ->
        AlertDialog(
            onDismissRequest = { invitation = null },
            title = { Text(stringResource(R.string.owner_device_confirm_title)) },
            text = {
                Text(
                    stringResource(
                        R.string.owner_device_confirm_message,
                        pending.serverUrl,
                        DateFormat.getDateTimeInstance().format(Date(pending.expiresAtEpochMillis)),
                    ),
                )
            },
            dismissButton = {
                TextButton(onClick = { invitation = null }) { Text(stringResource(R.string.common_cancel)) }
            },
            confirmButton = {
                Button(onClick = {
                    invitation = null
                    viewModel.pairOwnerDevice(activity)
                }) { Text(stringResource(R.string.owner_device_confirm)) }
            },
        )
    }
}
