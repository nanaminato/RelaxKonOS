package app.relaxkonos.mobile.ui.files

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.rememberTransformableState
import androidx.compose.foundation.gestures.transformable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import app.relaxkonos.mobile.R
import app.relaxkonos.mobile.core.net.RemoteEntry
import app.relaxkonos.mobile.data.isDecodableImage
import app.relaxkonos.mobile.ui.common.SectionCard
import app.relaxkonos.mobile.ui.common.UiMessage
import app.relaxkonos.mobile.ui.common.formatSize
import app.relaxkonos.mobile.ui.common.text
import app.relaxkonos.mobile.ui.icons.DesktopIcons
import app.relaxkonos.mobile.ui.theme.Layout
import app.relaxkonos.mobile.ui.theme.Radius
import app.relaxkonos.mobile.ui.theme.Spacing

/** The most zoom the full-screen viewer allows, so a pinch cannot lose the picture off-screen. */
private const val MAX_VIEWER_ZOOM = 8f

/**
 * What the file detail pane shows above the properties of the selected entry.
 *
 * Four states rather than "loading or loaded", because the two failures it can hit call for opposite
 * answers: a file the user is not allowed to read needs an administrator password, and one the platform
 * cannot decode needs to be told apart from an empty card.
 */
sealed interface ImagePreview {
    /** Nothing selected, or the selection is not an image: there is no preview to show. */
    data object Hidden : ImagePreview

    /**
     * The bytes are on their way: [receivedBytes] of [totalBytes], the latter being what the server
     * announced when it announced one.
     *
     * [thumbnail] is the small copy the server rendered first, drawn *under* the progress bar rather
     * than replaced by it: a picture that is already recognisable answers the question a progress bar
     * can only describe. It is `null` for a file the server has no thumbnail for, which is every
     * non-image and every server without the route.
     */
    data class Downloading(val receivedBytes: Long, val totalBytes: Long?, val thumbnail: Bitmap? = null) : ImagePreview

    /**
     * [placeholder] is the first, small rendering of the picture — the server's thumbnail when there
     * was one, otherwise a 96-pixel decode of the file that has just landed. [image] is the same
     * picture decoded for the box it is drawn in and arrives a moment later. Exactly one of them is
     * set while a decode is in progress, and a caller draws `image ?: placeholder`.
     */
    data class Ready(val placeholder: Bitmap?, val image: Bitmap?) : ImagePreview

    /** [needsElevation] separates "the user can unlock this" from "this file cannot be shown at all". */
    data class Unavailable(val message: UiMessage, val needsElevation: Boolean) : ImagePreview
}

/**
 * The picture in the selected file.
 *
 * Nothing is drawn for an entry that is not an image, so a text file's detail pane looks exactly as it
 * did before this existed.
 */
@Composable
fun FileImagePreview(viewModel: FilesViewModel, modifier: Modifier = Modifier) {
    val entry = viewModel.selected ?: return
    if (!entry.isDecodableImage()) {
        return
    }

    SectionCard(
        title = stringResource(R.string.files_preview_title),
        modifier = modifier,
        leading = DesktopIcons.fileFor(entry.name, isDirectory = false),
    ) {
        val preview = viewModel.preview
        PreviewFrame(viewModel, entry, preview)
        PreviewActions(viewModel, preview)
    }
}

/**
 * The preview box itself.
 *
 * The box keeps its height whatever state it is in, so the properties below it do not jump up and down
 * as the image loads, fails or arrives. It measures itself and tells the ViewModel, because that
 * measurement — not a constant — is what decides how much of the image is decoded: a phone's one-column
 * pane and a tablet's full-screen viewer want very different amounts of the same file.
 */
@Composable
private fun PreviewFrame(viewModel: FilesViewModel, entry: RemoteEntry, preview: ImagePreview) {
    val density = LocalDensity.current
    BoxWithConstraints(
        modifier = Modifier
            .fillMaxWidth()
            .height(Layout.previewHeight)
            .clip(RoundedCornerShape(Radius.md))
            .background(MaterialTheme.colorScheme.surfaceContainerHigh),
        contentAlignment = Alignment.Center,
    ) {
        LaunchedEffect(maxWidth, maxHeight) {
            with(density) { viewModel.setPreviewBounds(maxWidth.roundToPx(), maxHeight.roundToPx()) }
        }
        when (preview) {
            is ImagePreview.Ready -> {
                (preview.image ?: preview.placeholder)?.let { bitmap ->
                    Image(
                        bitmap = bitmap.asImageBitmap(),
                        contentDescription = entry.name,
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxSize().clickable { viewModel.openViewer() },
                    )
                }
            }

            is ImagePreview.Downloading -> if (preview.thumbnail == null) {
                DownloadProgress(preview)
            } else {
                // Not clickable while the transfer runs: opening the viewer needs the full-size decode
                // that has not happened yet, so a tap here would be a button that does nothing.
                Box(Modifier.fillMaxSize()) {
                    Image(
                        bitmap = preview.thumbnail.asImageBitmap(),
                        contentDescription = entry.name,
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxSize(),
                    )
                    DownloadProgressBar(preview, Modifier.align(Alignment.BottomCenter))
                }
            }

            is ImagePreview.Unavailable -> Text(
                text = preview.message.text(),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(Spacing.lg),
            )

            ImagePreview.Hidden -> Unit
        }
    }
}

/**
 * The only action the preview has: opening the file full-screen, which re-reads the same cached bytes
 * at the screen's size instead of fetching them again.
 */
@Composable
private fun PreviewActions(viewModel: FilesViewModel, preview: ImagePreview) {
    when (preview) {
        is ImagePreview.Ready -> TextButton(
            onClick = viewModel::openViewer,
            modifier = Modifier.fillMaxWidth(),
        ) {
            Text(stringResource(R.string.files_preview_open))
        }

        is ImagePreview.Unavailable -> Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (preview.needsElevation) {
                Button(onClick = { viewModel.reloadPreview(authorize = true) }) {
                    Text(stringResource(R.string.files_preview_authorize))
                }
            } else {
                TextButton(onClick = { viewModel.reloadPreview(authorize = false) }) {
                    Text(stringResource(R.string.common_retry))
                }
            }
        }

        else -> Unit
    }
}

/**
 * How far along the transfer is: a determinate bar whenever the server announced a length, and an
 * honest spinner when it did not.
 *
 * It is drawn in two places — as the whole content of the frame while there is nothing to look at,
 * and along the bottom edge of a thumbnail that is already on screen — and the ratio must read the
 * same in both, which is why it lives in one composable.
 */
@Composable
private fun DownloadProgressBar(preview: ImagePreview.Downloading, modifier: Modifier = Modifier) {
    val total = preview.totalBytes
    if (total != null && total > 0) {
        LinearProgressIndicator(
            progress = { (preview.receivedBytes.toFloat() / total).coerceIn(0f, 1f) },
            modifier = modifier.fillMaxWidth(),
        )
    } else {
        LinearProgressIndicator(modifier = modifier.fillMaxWidth())
    }
}

/** What a transfer shows when there is nothing to look at yet. */
@Composable
private fun DownloadProgress(preview: ImagePreview.Downloading) {
    Column(
        modifier = Modifier.padding(Spacing.lg),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(Spacing.sm),
    ) {
        DownloadProgressBar(preview)
        val total = preview.totalBytes
        if (total != null && total > 0) {
            Text(
                text = stringResource(
                    R.string.files_preview_progress,
                    formatSize(preview.receivedBytes).orEmpty(),
                    formatSize(total).orEmpty(),
                ),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Text(
            text = stringResource(R.string.files_preview_loading),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

/**
 * The image at the size of the screen.
 *
 * The bytes are already in the cache and already decoded for the card, so opening this is a second
 * decode of a local file rather than a second transfer — which is exactly what the cache is for. The
 * surface follows the theme instead of forcing black: the app has one palette, and a light-mode user
 * who opens a photo should not be dropped into a different one.
 *
 * Back closes it, because a `Dialog` handles the back press and answers it through [onDismissRequest];
 * the shell's navigation is untouched while it is open.
 */
@Composable
fun ImagePreviewViewer(viewModel: FilesViewModel) {
    if (!viewModel.viewerOpen) {
        return
    }
    val entry = viewModel.selected ?: return
    val preview = viewModel.preview as? ImagePreview.Ready ?: return
    val bitmap = preview.image ?: preview.placeholder ?: return
    val density = LocalDensity.current

    Dialog(
        onDismissRequest = viewModel::closeViewer,
        properties = DialogProperties(usePlatformDefaultWidth = false),
    ) {
        Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
            Column(Modifier.fillMaxSize()) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(Spacing.lg),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(Spacing.lg),
                ) {
                    Text(
                        text = entry.name,
                        style = MaterialTheme.typography.titleSmall,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    TextButton(onClick = viewModel::closeViewer) {
                        Text(stringResource(R.string.common_close))
                    }
                }

                BoxWithConstraints(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f)
                        .background(MaterialTheme.colorScheme.surfaceContainerHigh),
                    contentAlignment = Alignment.Center,
                ) {
                    LaunchedEffect(maxWidth, maxHeight) {
                        with(density) { viewModel.setPreviewBounds(maxWidth.roundToPx(), maxHeight.roundToPx()) }
                    }
                    ZoomableImage(bitmap, entry.name)
                }
            }
        }
    }
}

/**
 * The image with pinch-zoom and panning.
 *
 * Zoom starts at 1 (fit) and panning is dropped when it returns there, so a picture can never be left
 * scaled down or pushed off-screen with no way back: there is no state to recover from, only the
 * unzoomed view.
 */
@Composable
private fun ZoomableImage(bitmap: Bitmap, description: String) {
    var scale by remember { mutableStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }
    val transformable = rememberTransformableState { zoomChange, panChange, _ ->
        scale = (scale * zoomChange).coerceIn(1f, MAX_VIEWER_ZOOM)
        offset = if (scale <= 1f) Offset.Zero else offset + panChange
    }

    Image(
        bitmap = bitmap.asImageBitmap(),
        contentDescription = description,
        contentScale = ContentScale.Fit,
        modifier = Modifier
            .fillMaxSize()
            .graphicsLayer(
                scaleX = scale,
                scaleY = scale,
                translationX = offset.x,
                translationY = offset.y,
            )
            .transformable(state = transformable),
    )
}
