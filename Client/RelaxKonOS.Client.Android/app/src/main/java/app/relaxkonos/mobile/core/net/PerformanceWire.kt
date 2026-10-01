package app.relaxkonos.mobile.core.net

import org.json.JSONArray
import org.json.JSONObject

internal object PerformanceWire {
    fun snapshot(body: String): PerformanceSnapshot = snapshot(JSONObject(body))
    fun info(body: String): PerformanceInfo = info(JSONObject(body))
    fun history(body: String): List<PerformanceSnapshot> = JSONArray(body).objects(60, ::snapshot).also { samples ->
        require(samples.zipWithNext().all { (a, b) -> a.sequence < b.sequence })
    }
    fun addresses(body: String): List<NetworkAddress> = JSONArray(body).objects { json ->
        NetworkAddress(json.string("interfaceName"), json.string("address"), json.string("family").also { require(it == "IPv4" || it == "IPv6") })
    }

    private fun performanceCapabilities(json: JSONObject): PerformanceCapabilities = PerformanceCapabilities(
        perLogicalCpu = json.boolean("perLogicalCpu"),
        cpuFrequency = json.boolean("cpuFrequency"),
        cpuIowait = json.boolean("cpuIowait"),
        diskIo = json.boolean("diskIo"),
        diskLatency = json.boolean("diskLatency"),
        diskQueueLength = json.boolean("diskQueueLength"),
        networkErrors = json.boolean("networkErrors"),
        gpu = json.boolean("gpu"),
    )

    private fun cpuInfo(json: JSONObject): CpuInfo = CpuInfo(
        model = json.optional("model") { json.string("model") },
        physicalCoreCount = json.optional("physicalCoreCount") { json.integer("physicalCoreCount") },
        logicalProcessorCount = json.integer("logicalProcessorCount"),
        baseFrequencyMHz = json.optional("baseFrequencyMHz") { json.number("baseFrequencyMHz", percent = false) },
        virtualizationEnabled = json.optional("virtualizationEnabled") { json.boolean("virtualizationEnabled") },
        socketCount = json.optional("socketCount") { json.integer("socketCount") },
        l1CacheBytes = json.optional("l1CacheBytes") { json.long("l1CacheBytes") },
        l2CacheBytes = json.optional("l2CacheBytes") { json.long("l2CacheBytes") },
        l3CacheBytes = json.optional("l3CacheBytes") { json.long("l3CacheBytes") },
    )

    private fun memoryInfo(json: JSONObject): MemoryInfo = MemoryInfo(
        totalBytes = json.long("totalBytes"),
        swapTotalBytes = json.optional("swapTotalBytes") { json.long("swapTotalBytes") },
    )

    private fun filesystemInfo(json: JSONObject): FilesystemInfo = FilesystemInfo(
        id = json.string("id"),
        name = json.string("name"),
        mountPoint = json.string("mountPoint"),
    )

    private fun diskInfo(json: JSONObject): DiskInfo = DiskInfo(
        id = json.string("id"),
        name = json.string("name"),
        model = json.optional("model") { json.string("model") },
        filesystemIds = json.getJSONArray("filesystemIds").values { require(it is String); it },
    )

    private fun networkInterfaceInfo(json: JSONObject): NetworkInterfaceInfo = NetworkInterfaceInfo(
        id = json.string("id"),
        name = json.string("name"),
        linkSpeedBitsPerSecond = json.optional("linkSpeedBitsPerSecond") { json.long("linkSpeedBitsPerSecond") },
        addresses = json.getJSONArray("addresses").values { require(it is String); it },
    )

    private fun info(json: JSONObject): PerformanceInfo = PerformanceInfo(
        cpu = cpuInfo(json.getJSONObject("cpu")),
        memory = memoryInfo(json.getJSONObject("memory")),
        filesystems = json.getJSONArray("filesystems").objects { filesystemInfo(it) },
        disks = json.getJSONArray("disks").objects { diskInfo(it) },
        networks = json.getJSONArray("networks").objects { networkInterfaceInfo(it) },
        capabilities = performanceCapabilities(json.getJSONObject("capabilities")),
    )

    private fun cpuRealtimeMetrics(json: JSONObject): CpuRealtimeMetrics = CpuRealtimeMetrics(
        totalPercent = json.number("totalPercent", percent = true),
        userPercent = json.optional("userPercent") { json.number("userPercent", percent = true) },
        systemPercent = json.optional("systemPercent") { json.number("systemPercent", percent = true) },
        idlePercent = json.optional("idlePercent") { json.number("idlePercent", percent = true) },
        iowaitPercent = json.optional("iowaitPercent") { json.number("iowaitPercent", percent = true) },
        perLogicalCpuPercent = json.getJSONArray("perLogicalCpuPercent").values { number(it, percent = true) },
        currentFrequencyMHz = json.optional("currentFrequencyMHz") { json.number("currentFrequencyMHz", percent = false) },
        processCount = json.optional("processCount") { json.integer("processCount") },
        threadCount = json.optional("threadCount") { json.integer("threadCount") },
        handleCount = json.optional("handleCount") { json.long("handleCount") },
    )

    private fun memoryRealtimeMetrics(json: JSONObject): MemoryRealtimeMetrics = MemoryRealtimeMetrics(
        totalBytes = json.long("totalBytes"),
        usedBytes = json.long("usedBytes"),
        availableBytes = json.long("availableBytes"),
        cachedBytes = json.optional("cachedBytes") { json.long("cachedBytes") },
        bufferedBytes = json.optional("bufferedBytes") { json.long("bufferedBytes") },
        swapUsedBytes = json.optional("swapUsedBytes") { json.long("swapUsedBytes") },
        swapTotalBytes = json.optional("swapTotalBytes") { json.long("swapTotalBytes") },
    )

    private fun filesystemUsage(json: JSONObject): FilesystemUsage = FilesystemUsage(
        id = json.string("id"),
        totalBytes = json.long("totalBytes"),
        usedBytes = json.long("usedBytes"),
        availableBytes = json.long("availableBytes"),
        percent = json.number("percent", percent = true),
    )

    private fun diskRealtimeMetrics(json: JSONObject): DiskRealtimeMetrics = DiskRealtimeMetrics(
        id = json.string("id"),
        readBytesPerSecond = json.long("readBytesPerSecond"),
        writeBytesPerSecond = json.long("writeBytesPerSecond"),
        readIops = json.number("readIops", percent = false),
        writeIops = json.number("writeIops", percent = false),
        activityPercent = json.optional("activityPercent") { json.number("activityPercent", percent = true) },
        queueLength = json.optional("queueLength") { json.number("queueLength", percent = false) },
        latencyMs = json.optional("latencyMs") { json.number("latencyMs", percent = false) },
    )

    private fun networkRealtimeMetrics(json: JSONObject): NetworkRealtimeMetrics = NetworkRealtimeMetrics(
        id = json.string("id"),
        bytesReceived = json.long("bytesReceived"),
        bytesSent = json.long("bytesSent"),
        receiveBytesPerSecond = json.long("receiveBytesPerSecond"),
        sendBytesPerSecond = json.long("sendBytesPerSecond"),
        receivePackets = json.long("receivePackets"),
        sendPackets = json.long("sendPackets"),
        receiveErrors = json.optional("receiveErrors") { json.long("receiveErrors") },
        sendErrors = json.optional("sendErrors") { json.long("sendErrors") },
        receiveDropped = json.optional("receiveDropped") { json.long("receiveDropped") },
        sendDropped = json.optional("sendDropped") { json.long("sendDropped") },
    )

    private fun performanceHealth(json: JSONObject): PerformanceHealth = PerformanceHealth(
        isStale = json.boolean("isStale"),
        lastSuccessfulSampleAt = json.optional("lastSuccessfulSampleAt") { json.string("lastSuccessfulSampleAt").also { IsoInstant.requireEpochMillis(it) } },
        error = json.optional("error") { json.string("error") },
    )

    private fun snapshot(json: JSONObject): PerformanceSnapshot = PerformanceSnapshot(
        sequence = json.long("sequence"),
        timestamp = json.string("timestamp").also { IsoInstant.requireEpochMillis(it) },
        cpu = cpuRealtimeMetrics(json.getJSONObject("cpu")),
        memory = memoryRealtimeMetrics(json.getJSONObject("memory")),
        filesystems = json.getJSONArray("filesystems").objects { filesystemUsage(it) },
        disks = json.getJSONArray("disks").objects { diskRealtimeMetrics(it) },
        networks = json.getJSONArray("networks").objects { networkRealtimeMetrics(it) },
        uptimeSeconds = json.long("uptimeSeconds"),
        health = performanceHealth(json.getJSONObject("health")),
    )

    private fun JSONObject.string(key: String): String = get(key).let { require(it is String); it }
    private fun JSONObject.boolean(key: String): Boolean = get(key).let { require(it is Boolean); it }
    private fun JSONObject.long(key: String): Long = get(key).let {
        require(it is Int || it is Long); (it as Number).toLong().also { value -> require(value >= 0) }
    }
    private fun JSONObject.integer(key: String): Int = long(key).also { require(it <= Int.MAX_VALUE) }.toInt()
    private fun JSONObject.number(key: String, percent: Boolean): Double = number(get(key), percent)
    private fun number(value: Any, percent: Boolean): Double {
        require(value is Number)
        return value.toDouble().also { require(it.isFinite() && it >= 0 && (!percent || it <= 100)) }
    }
    private fun <T> JSONObject.optional(key: String, read: () -> T): T? = if (get(key) == JSONObject.NULL) null else read()
    private fun <T> JSONArray.objects(maximum: Int = 4096, read: (JSONObject) -> T): List<T> {
        require(length() <= maximum); return List(length()) { read(getJSONObject(it)) }
    }
    private fun <T> JSONArray.values(read: (Any) -> T): List<T> {
        require(length() <= 4096); return List(length()) { read(get(it)) }
    }
}
