package app.relaxkonos.mobile.core.net

/** Complete current PerformanceDtos contract; optional readings remain null, never synthetic zero. */
data class PerformanceCapabilities(
    val perLogicalCpu: Boolean,
    val cpuFrequency: Boolean,
    val cpuIowait: Boolean,
    val diskIo: Boolean,
    val diskLatency: Boolean,
    val diskQueueLength: Boolean,
    val networkErrors: Boolean,
    val gpu: Boolean,
)

data class CpuInfo(
    val model: String?,
    val physicalCoreCount: Int?,
    val logicalProcessorCount: Int,
    val baseFrequencyMHz: Double?,
    val virtualizationEnabled: Boolean?,
    val socketCount: Int?,
    val l1CacheBytes: Long?,
    val l2CacheBytes: Long?,
    val l3CacheBytes: Long?,
)

data class MemoryInfo(
    val totalBytes: Long,
    val swapTotalBytes: Long?,
)

data class FilesystemInfo(
    val id: String,
    val name: String,
    val mountPoint: String,
)

data class DiskInfo(
    val id: String,
    val name: String,
    val model: String?,
    val filesystemIds: List<String>,
)

data class NetworkInterfaceInfo(
    val id: String,
    val name: String,
    val linkSpeedBitsPerSecond: Long?,
    val addresses: List<String>,
)

data class PerformanceInfo(
    val cpu: CpuInfo,
    val memory: MemoryInfo,
    val filesystems: List<FilesystemInfo>,
    val disks: List<DiskInfo>,
    val networks: List<NetworkInterfaceInfo>,
    val capabilities: PerformanceCapabilities,
)

data class CpuRealtimeMetrics(
    val totalPercent: Double,
    val userPercent: Double?,
    val systemPercent: Double?,
    val idlePercent: Double?,
    val iowaitPercent: Double?,
    val perLogicalCpuPercent: List<Double>,
    val currentFrequencyMHz: Double?,
    val processCount: Int?,
    val threadCount: Int?,
    val handleCount: Long?,
)

data class MemoryRealtimeMetrics(
    val totalBytes: Long,
    val usedBytes: Long,
    val availableBytes: Long,
    val cachedBytes: Long?,
    val bufferedBytes: Long?,
    val swapUsedBytes: Long?,
    val swapTotalBytes: Long?,
)

data class FilesystemUsage(
    val id: String,
    val totalBytes: Long,
    val usedBytes: Long,
    val availableBytes: Long,
    val percent: Double,
)

data class DiskRealtimeMetrics(
    val id: String,
    val readBytesPerSecond: Long,
    val writeBytesPerSecond: Long,
    val readIops: Double,
    val writeIops: Double,
    val activityPercent: Double?,
    val queueLength: Double?,
    val latencyMs: Double?,
)

data class NetworkRealtimeMetrics(
    val id: String,
    val bytesReceived: Long,
    val bytesSent: Long,
    val receiveBytesPerSecond: Long,
    val sendBytesPerSecond: Long,
    val receivePackets: Long,
    val sendPackets: Long,
    val receiveErrors: Long?,
    val sendErrors: Long?,
    val receiveDropped: Long?,
    val sendDropped: Long?,
)

data class PerformanceHealth(
    val isStale: Boolean,
    val lastSuccessfulSampleAt: String?,
    val error: String?,
)

data class PerformanceSnapshot(
    val sequence: Long,
    val timestamp: String,
    val cpu: CpuRealtimeMetrics,
    val memory: MemoryRealtimeMetrics,
    val filesystems: List<FilesystemUsage>,
    val disks: List<DiskRealtimeMetrics>,
    val networks: List<NetworkRealtimeMetrics>,
    val uptimeSeconds: Long,
    val health: PerformanceHealth,
) {
    val cpuPercent: Double get() = cpu.totalPercent
    val memoryUsedBytes: Long get() = memory.usedBytes
    val memoryTotalBytes: Long get() = memory.totalBytes
    val isStale: Boolean get() = health.isStale
    val lastSampleMillis: Long? get() = health.lastSuccessfulSampleAt?.let(IsoInstant::toEpochMillis)
}

data class NetworkAddress(val interfaceName: String, val address: String, val family: String)
