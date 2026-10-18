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
import androidx.compose.foundation.layout.imePadding
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
import androidx.activity.compose.BackHandler
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.rememberUpdatedState
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import app.relaxkonos.mobile.ui.common.UiMessage
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
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
 * parsed HTTP(S) server and invitation expiry and must explicitly confirm before key generation or
 * network enrollment starts.
 */
@Composable
fun OwnerDevicePairingScreen(modifier: Modifier = Modifier, onClose: () -> Unit) {
    val activity = androidx.activity.compose.LocalActivity.current as? FragmentActivity ?: return
    val context = LocalContext.current
    val viewModel: LoginViewModel = viewModel()
    var scanMessage by remember { mutableStateOf<Int?>(null) }
    val scans = remember { PairingScanState() }
    var imageTicket by remember { mutableStateOf<PairingScanState.Ticket?>(null) }
    var imageScanner by remember { mutableStateOf<com.google.mlkit.vision.barcode.BarcodeScanner?>(null) }
    DisposableEffect(scans) {
        scans.attach()
        onDispose { scans.detach(); imageScanner?.close(); imageScanner = null }
    }
    val options = remember {
        BarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build()
    }
    fun finishScan(ticket: PairingScanState.Ticket, value: String?, failure: Int? = null) {
        if (!scans.finish(ticket, viewModel.ownerDevicePairingCode, viewModel.isLoggingIn)) return
        if (failure != null) scanMessage = failure
        else if (value.isNullOrBlank()) scanMessage = R.string.owner_device_scan_no_code
        else { viewModel.changeOwnerDevicePairingCode(value); scanMessage = null }
    }
    fun scanImage(uri: Uri, ticket: PairingScanState.Ticket) {
        if (!scans.owns(ticket)) return
        val scanner = runCatching { BarcodeScanning.getClient(options) }.getOrElse {
            finishScan(ticket, null, R.string.owner_device_scan_failed)
            return
        }
        imageScanner = scanner
        runCatching {
                val image = InputImage.fromFilePath(context, uri)
                scanner.process(image)
                    .addOnSuccessListener { barcodes ->
                        scanner.close()
                        if (imageScanner === scanner) imageScanner = null
                        finishScan(ticket, barcodes.firstOrNull { !it.rawValue.isNullOrBlank() }?.rawValue)
                    }
                    .addOnCanceledListener {
                        scanner.close()
                        if (imageScanner === scanner) imageScanner = null
                        scans.cancel(ticket)
                    }
                    .addOnFailureListener {
                        scanner.close()
                        if (imageScanner === scanner) imageScanner = null
                        finishScan(ticket, null, R.string.owner_device_scan_failed)
                    }
            }.onFailure {
                scanner.close()
                if (imageScanner === scanner) imageScanner = null
                finishScan(ticket, null, R.string.owner_device_scan_failed)
            }
    }
    val imagePicker = rememberLauncherForActivityResult(rememberUsageOpenDocument("OwnerDevicePairingScreen.pairing-image")) { uri ->
        val ticket = imageTicket
        imageTicket = null
        if (ticket != null) {
            if (uri == null) scans.cancel(ticket) else scanImage(uri, ticket)
        }
    }
    val codeScanner = remember(activity) {
        GmsBarcodeScanning.getClient(activity,
            GmsBarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build())
    }
    OwnerDevicePairingContent(
        code = { viewModel.ownerDevicePairingCode }, busy = { viewModel.isLoggingIn }, scanning = { scans.busy },
        message = viewModel.message, scanMessage = scanMessage,
        onDismissMessage = viewModel::dismissMessage, onDismissScanMessage = { scanMessage = null },
        onCodeChange = { viewModel.changeOwnerDevicePairingCode(it); scanMessage = null },
        onPair = { viewModel.pairOwnerDevice(activity, it) },
        onCamera = {
            scans.begin(viewModel.ownerDevicePairingCode, viewModel.isLoggingIn)?.let { ticket ->
                scanMessage = null
                runCatching {
                    codeScanner.startScan()
                        .addOnSuccessListener { barcode -> finishScan(ticket, barcode.rawValue) }
                        .addOnFailureListener { finishScan(ticket, null, R.string.owner_device_scan_failed) }
                        .addOnCanceledListener { scans.cancel(ticket) }
                }.onFailure { finishScan(ticket, null, R.string.owner_device_scan_failed) }
            }
        },
        onImage = {
            scans.begin(viewModel.ownerDevicePairingCode, viewModel.isLoggingIn)?.let { ticket ->
                scanMessage = null
                imageTicket = ticket
                runCatching { imagePicker.launch(arrayOf("image/*")) }
                    .onFailure { imageTicket = null; finishScan(ticket, null, R.string.owner_device_scan_failed) }
            }
        },
        onClose = { scans.detach(); imageScanner?.close(); onClose() }, modifier = modifier,
    )
}

private class PairingConfirmation(val payload: String, val invitation: OwnerDevicePairingInvitation)

@Composable
internal fun OwnerDevicePairingContent(
    code: () -> String, busy: () -> Boolean, scanning: () -> Boolean, message: UiMessage?, scanMessage: Int?,
    onDismissMessage: () -> Unit, onDismissScanMessage: () -> Unit,
    onCodeChange: (String) -> Unit, onPair: (String) -> Unit,
    onCamera: () -> Unit, onImage: () -> Unit, onClose: () -> Unit, modifier: Modifier = Modifier,
) {
    val currentCode by rememberUpdatedState(code)
    val currentBusy by rememberUpdatedState(busy)
    val currentScanning by rememberUpdatedState(scanning)
    val currentPair by rememberUpdatedState(onPair)
    val lifecycleOwner = LocalLifecycleOwner.current
    var attached by remember { mutableStateOf(false) }
    var closed by remember { mutableStateOf(false) }
    var invitation by remember { mutableStateOf<PairingConfirmation?>(null) }
    var invalid by remember { mutableStateOf(false) }
    DisposableEffect(lifecycleOwner) {
        attached = true
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_PAUSE) invitation = null
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { attached = false; invitation = null; lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    fun canAct() = attached && !closed && !currentBusy() &&
        lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)
    fun close() {
        if (!canAct()) return
        closed = true
        invitation = null
        onClose()
    }
    BackHandler { close() }
    Column(
        modifier = modifier.fillMaxSize().safeDrawingPadding().imePadding().verticalScroll(rememberScrollState()).padding(Spacing.lg),
        verticalArrangement = Arrangement.spacedBy(Spacing.lg),
    ) {
        ScreenHeader(
            title = stringResource(R.string.owner_device_add_title),
            subtitle = stringResource(R.string.owner_device_add_subtitle),
            onBack = ::close,
        )

        // This flow's own failures belong on the screen that produced them. It shares its view model
        // with the login screen, so without this every pairing failure was reported as a dialog on the
        // login screen — shown only after the user had navigated away from where it happened.
        message?.let { banner ->
            ActionFeedback(message = banner, onRetry = null, onDismiss = onDismissMessage)
        }

        OutlinedTextField(
            value = code(),
            onValueChange = {
                if (canAct() && !currentScanning()) { invitation = null; invalid = false; onCodeChange(it) }
            },
            label = { Text(stringResource(R.string.owner_device_pairing_code)) },
            modifier = Modifier.fillMaxWidth(),
            minLines = 4,
            enabled = !busy() && !scanning(),
            shape = MaterialTheme.shapes.medium,
        )

        OutlinedButton(
            onClick = { if (canAct() && !currentScanning()) { invitation = null; onCamera() } },
            enabled = !busy() && !scanning(), modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(R.string.owner_device_scan_camera)) }
        OutlinedButton(
            onClick = { if (canAct() && !currentScanning()) { invitation = null; onImage() } },
            enabled = !busy() && !scanning(), modifier = Modifier.fillMaxWidth(),
        ) { Text(stringResource(R.string.owner_device_scan_image)) }
        OperationMessageDialog(
            if (invalid) stringResource(R.string.owner_device_pairing_invalid) else scanMessage?.let { stringResource(it) },
            onDismiss = { invalid = false; onDismissScanMessage() },
        )

        Button(
            onClick = {
                if (canAct() && !currentScanning()) {
                    val payload = currentCode()
                    invitation = runCatching { PairingConfirmation(payload, OwnerDevicePairingInvitation.parse(payload)) }
                        .getOrElse { invalid = true; null }
                }
            },
            enabled = !busy() && !scanning(),
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
                        pending.invitation.serverUrl,
                        DateFormat.getDateTimeInstance().format(Date(pending.invitation.expiresAtEpochMillis)),
                    ),
                )
            },
            dismissButton = {
                TextButton(onClick = { invitation = null }) { Text(stringResource(R.string.common_cancel)) }
            },
            confirmButton = {
                Button(onClick = {
                    if (invitation !== pending) return@Button
                    invitation = null
                    if (!canAct() || currentScanning() || currentCode() != pending.payload) return@Button
                    if (runCatching { OwnerDevicePairingInvitation.parse(pending.payload) }.isFailure) {
                        invalid = true
                    } else currentPair(pending.payload)
                }) { Text(stringResource(R.string.owner_device_confirm)) }
            },
        )
    }
}
