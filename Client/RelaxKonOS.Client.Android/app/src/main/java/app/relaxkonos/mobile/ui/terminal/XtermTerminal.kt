package app.relaxkonos.mobile.ui.terminal

import android.annotation.SuppressLint
import android.graphics.Color
import android.webkit.JavascriptInterface
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.runtime.Composable
import androidx.compose.runtime.key
import androidx.compose.runtime.remember
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import org.json.JSONObject
import java.io.ByteArrayInputStream
import app.relaxkonos.mobile.R
import android.view.MotionEvent

/** This bridge is exposed only to bundled assets; the WebView cannot navigate or fetch remotely. */
internal class TerminalBridge(private val view: WebView) {
    var send: (String) -> Unit = {}
    var resize: (Int, Int) -> Unit = { _, _ -> }
    var active = true
    @JavascriptInterface fun send(value: String) { view.post { if (active) send.invoke(value) } }
    @JavascriptInterface fun resize(columns: Int, rows: Int) {
        view.post { if (active) resize.invoke(columns, rows) }
    }
}

@SuppressLint("SetJavaScriptEnabled")
internal class XtermView(context: android.content.Context) : WebView(context) {
    val bridge = TerminalBridge(this)
    private var ready = false
    var onReady: () -> Unit = {}
    private var applied = ""
    private var output = ""
    private var fontSize = 13f
    private var online = false

    init {
        setBackgroundColor(Color.rgb(16, 24, 32))
        settings.javaScriptEnabled = true
        settings.allowFileAccess = false
        settings.allowContentAccess = false
        settings.blockNetworkLoads = true
        addJavascriptInterface(bridge, "NativeTerminal")
        webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest) = true
            @Suppress("DEPRECATION")
            override fun shouldOverrideUrlLoading(view: WebView, url: String) = true
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse {
                val uri = request.url
                val name = uri.path.orEmpty().removePrefix("/terminal/")
                if (uri.scheme != "https" || uri.host != "terminal.local" ||
                    name !in setOf("index.html", "terminal.js", "xterm.js", "addon-fit.js", "xterm.css")) {
                    return WebResourceResponse("text/plain", "UTF-8", 403, "Forbidden", emptyMap(), ByteArrayInputStream(byteArrayOf()))
                }
                val type = when { name.endsWith(".html") -> "text/html"; name.endsWith(".css") -> "text/css"; else -> "application/javascript" }
                return WebResourceResponse(type, "UTF-8", context.assets.open("terminal/$name"))
            }
            override fun onPageFinished(view: WebView, url: String) {
                if (!bridge.active) return
                ready = true
                flush()
                onReady()
            }
        }
        loadUrl("https://terminal.local/terminal/index.html")
    }

    fun update(value: String, size: Float, connected: Boolean) {
        output = value
        fontSize = size
        online = connected
        flush()
    }

    private fun flush() {
        if (!ready) return
        val reset = !output.startsWith(applied)
        val delta = if (reset) output else output.substring(applied.length)
        applied = output
        val latestLabel = JSONObject.quote(context.getString(R.string.terminal_latest_output))
        evaluateJavascript("window.updateTerminal(${JSONObject.quote(delta)},$reset,$fontSize,$online,$latestLabel)", null)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        // Let the terminal handle its own vertical gesture instead of an outer Compose container.
        if (event.actionMasked == MotionEvent.ACTION_DOWN) parent?.requestDisallowInterceptTouchEvent(true)
        val handled = super.onTouchEvent(event)
        if (event.actionMasked == MotionEvent.ACTION_UP || event.actionMasked == MotionEvent.ACTION_CANCEL)
            parent?.requestDisallowInterceptTouchEvent(false)
        return handled
    }

    fun release() {
        onReady = {}
        bridge.active = false
        removeJavascriptInterface("NativeTerminal")
        stopLoading()
        destroy()
    }
}

@Composable
internal fun XtermTerminal(sessionId: String, output: String, fontSize: Float, connected: Boolean,
    onSend: (String) -> Unit, onResize: (Int, Int) -> Unit, modifier: Modifier = Modifier) {
    key(sessionId) {
        var initialized by remember { mutableStateOf(false) }
        Box(modifier) {
            AndroidView(factory = { XtermView(it).apply { onReady = { initialized = true } } }, modifier = Modifier.fillMaxSize(),
                onRelease = { it.release() }, update = {
                    it.bridge.send = onSend
                    it.bridge.resize = onResize
                    it.update(output, fontSize, connected)
                })
            if (!initialized) TerminalLoading()
        }
    }
}
