package app.relaxkonos.mobile.core.net

import org.json.JSONObject

data class ProcessKillResult(val success: Boolean, val requiresElevation: Boolean, val problemCode: String, val error: String?)

enum class ProcessSort(val wireName: String) { Cpu("cpu"), Memory("memory"), Name("name"), Pid("pid") }

internal object SystemProcessWire {
    fun startTime(value: String): String { IsoInstant.requireEpochMillis(value); return value }
    fun page(body: String): ProcessPage {
        val root = JSONObject(body); val items = root.getJSONArray("items")
        require(items.length() <= 500)
        val processes = List(items.length()) { index -> items.getJSONObject(index).let { item ->
            val cpu = item.get("cpuPercent").let { require(it is Number); it.toDouble().also { value -> require(value.isFinite() && value in 0.0..100.0) } }
            RemoteProcess(item.integer("id").also { require(it > 0) }, item.getString("name"), cpu,
                item.long("memoryBytes").also { require(it >= 0) }, item.nullable("userName"),
                item.integer("threadCount").also { require(it >= 0) }, item.nullable("startTime")?.let(::startTime))
        } }
        return ProcessPage(processes, root.integer("totalCount").also { require(it >= processes.size) }, startTime(root.getString("sampledAt")))
    }
    fun kill(body: String): ProcessKillResult = JSONObject(body).let { json ->
        val success = json.boolean("success"); val elevation = json.boolean("requiresElevation"); val code = json.getString("problemCode")
        require(!success || !elevation && code.isEmpty())
        require(success || code.isNotBlank())
        ProcessKillResult(success, elevation, code, json.nullable("error"))
    }
    private fun JSONObject.nullable(name: String): String? = get(name).let { if (it == JSONObject.NULL) null else { require(it is String); it } }
    private fun JSONObject.boolean(name: String): Boolean = get(name).let { require(it is Boolean); it }
    private fun JSONObject.long(name: String): Long = get(name).let {
        require(it is Int || it is Long); (it as Number).toLong()
    }
    private fun JSONObject.integer(name: String) = long(name).also { require(it in Int.MIN_VALUE..Int.MAX_VALUE) }.toInt()
}
