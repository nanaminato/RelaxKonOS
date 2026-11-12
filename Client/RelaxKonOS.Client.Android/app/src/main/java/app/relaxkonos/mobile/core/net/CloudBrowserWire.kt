package app.relaxkonos.mobile.core.net

import java.net.URLEncoder
import java.util.UUID
import org.json.JSONObject

data class CloudEntry(val reference: String, val name: String, val isDirectory: Boolean, val size: Long?)
data class CloudDirectory(val entries: List<CloudEntry>, val nextCursor: String?)
object CloudBrowserWire {
    private fun encode(value: String) = URLEncoder.encode(value, "UTF-8")
    fun connection(id: String) = "${CloudAuthorizationWire.ROOT}/connections/${UUID.fromString(id)}"
    fun entries(id: String, reference: String, cursor: String?) =
        "${connection(id)}/entries?reference=${encode(reference)}" + (cursor?.let { "&cursor=${encode(it)}" } ?: "")
    fun download(id: String, reference: String) = "${connection(id)}/download?reference=${encode(reference)}"
    fun directory(text: String): CloudDirectory = JSONObject(text).let { root ->
        val entries = root.getJSONArray("entries")
        CloudDirectory((0 until entries.length()).map { index -> entries.getJSONObject(index).let {
            CloudEntry(it.getString("reference"), it.getString("name"), it.getBoolean("isDirectory"),
                if (it.isNull("size")) null else it.getLong("size"))
        } }, if (root.isNull("nextCursor")) null else root.getString("nextCursor"))
    }
}
