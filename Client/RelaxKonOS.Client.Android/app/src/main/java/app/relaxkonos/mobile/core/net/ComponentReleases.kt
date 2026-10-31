package app.relaxkonos.mobile.core.net

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.ensureActive
import org.json.JSONArray
import java.net.HttpURLConnection
import java.net.URL
import kotlin.coroutines.coroutineContext

data class ComponentRelease(val version: String, val rid: String, val downloadUrl: String, val fileName: String)

/** Fixed official sources only; discovery does not authorize installation. */
object ComponentReleases {
    fun rid(state: ComponentState): String {
        val target = state.target.split(" / ")
        require(target.size == 3)
        val rid = (if (target[0] == "windows") "win" else target[0]) + "-" + target[1]
        require(rid in setOf("win-x64", "win-arm64", "linux-x64", "linux-arm64"))
        return rid
    }
    private fun expected(component: String, rid: String, version: String): ComponentRelease {
        require(rid in setOf("win-x64", "win-arm64", "linux-x64", "linux-arm64"))
        val windows = rid.startsWith("win-")
        if (component == "nginx") {
            require(windows && Regex("[0-9]+\\.[0-9]+\\.[0-9]+").matches(version))
            val file = "nginx-$version.zip"
            return ComponentRelease(version, rid, "https://nginx.org/download/$file", file)
        }
        require(component in setOf("frpc", "frps") && Regex("v[0-9]+\\.[0-9]+\\.[0-9]+").matches(version))
        val file = "frp_${version.drop(1)}_${if (windows) "windows" else "linux"}_${if (rid.endsWith("arm64")) "arm64" else "amd64"}.${if (windows) "zip" else "tar.gz"}"
        return ComponentRelease(version, rid, "https://github.com/fatedier/frp/releases/download/$version/$file", file)
    }
    fun server(payload: String, component: String, rid: String): List<ComponentRelease> {
        val rows = JSONArray(payload)
        return (0 until rows.length()).map { index ->
            val row = rows.getJSONObject(index)
            val release = expected(component, rid, row.getString("version"))
            require(row.getString("rid") == rid && row.getString("downloadUrl") == release.downloadUrl && row.getString("fileName") == release.fileName)
            release
        }.distinctBy { it.version }
    }
    fun github(payload: String, component: String, rid: String): List<ComponentRelease> {
        val rows = JSONArray(payload)
        return (0 until rows.length()).mapNotNull { index ->
            val row = rows.getJSONObject(index)
            if (row.getBoolean("draft") || row.getBoolean("prerelease")) return@mapNotNull null
            val tag = row.getString("tag_name")
            if (!Regex("v[0-9]+\\.[0-9]+\\.[0-9]+").matches(tag)) return@mapNotNull null
            val release = expected(component, rid, tag)
            val assets = row.getJSONArray("assets")
            release.takeIf { (0 until assets.length()).any { i ->
                val asset = assets.getJSONObject(i)
                asset.getString("name") == release.fileName && asset.getString("browser_download_url") == release.downloadUrl
            } }
        }
    }
    fun nginx(html: String, rid: String): List<ComponentRelease> =
        Regex("(?:https://nginx\\.org)?/download/nginx-([0-9]+\\.[0-9]+\\.[0-9]+)\\.zip(?=[\"'])")
            .findAll(html).map { expected("nginx", rid, it.groupValues[1]) }.distinctBy { it.version }.toList()

    fun newer(version: String, current: String?): Boolean {
        fun parts(value: String?) = value?.removePrefix("v")?.takeIf { Regex("[0-9]+\\.[0-9]+\\.[0-9]+").matches(it) }?.split('.')?.map { it.toBigInteger() }
        val candidate = parts(version) ?: return false
        val installed = parts(current) ?: return false
        return candidate.zip(installed).firstOrNull { it.first != it.second }?.let { it.first > it.second } ?: false
    }
    suspend fun official(component: String, rid: String): List<ComponentRelease> = withContext(Dispatchers.IO) {
        suspend fun read(url: String): String {
            coroutineContext.ensureActive()
            val connection = URL(url).openConnection() as HttpURLConnection
            connection.connectTimeout = 8000
            connection.readTimeout = 8000
            connection.instanceFollowRedirects = false
            connection.setRequestProperty("User-Agent", "RelaxKonOS/1.0")
            try {
                require(connection.responseCode == 200)
                return connection.inputStream.use { stream ->
                    val output = java.io.ByteArrayOutputStream()
                    val buffer = ByteArray(8192)
                    while (true) {
                        coroutineContext.ensureActive()
                        val count = stream.read(buffer)
                        if (count < 0) break
                        require(output.size() + count <= 4 * 1024 * 1024)
                        output.write(buffer, 0, count)
                    }
                    output.toString("UTF-8")
                }
            } finally { connection.disconnect() }
        }
        if (component == "nginx") nginx(read("https://nginx.org/en/download.html"), rid)
        else {
            require(component in setOf("frpc", "frps"))
            buildList {
                for (page in 1..3) {
                    val payload = read("https://api.github.com/repos/fatedier/frp/releases?per_page=30&page=$page")
                    addAll(github(payload, component, rid))
                    if (JSONArray(payload).length() < 30) break
                }
            }.distinctBy { it.version }
        }
    }
}
