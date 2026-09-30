package app.relaxkonos.mobile.servercenter

import java.util.Base64

data class SshDiskSnapshot(val name: String, val usedBytes: Long, val totalBytes: Long)

data class SshSystemSnapshot(
    val cpuPercent: Double,
    val memoryUsedBytes: Long,
    val memoryTotalBytes: Long,
    val uptimeSeconds: Long,
    val system: String,
    val disks: List<SshDiskSnapshot>,
)

/** Fixed read-only probes; Windows uses encoded PowerShell regardless of the SSH default shell. */
object SshSystemProbe {
    suspend fun read(transport: ServerCenterSshTransport): SshSystemSnapshot? {
        val platform = transport.run("uname -s")
        val command = if (platform.succeeded && platform.standardOutput.trim() == "Linux") linuxCommand else windowsCommand
        val result = transport.run(command)
        return if (result.succeeded) parse(result.standardOutput) else null
    }

    fun parse(output: String): SshSystemSnapshot? {
        val values = output.lineSequence().mapNotNull {
            it.trim().split('=', limit = 2).takeIf { pair -> pair.size == 2 }
        }.associate { it[0] to it[1] }
        val cpu = values["cpu"]?.toDoubleOrNull()?.takeIf { it.isFinite() && it in 0.0..100.0 } ?: return null
        val total = values["memoryTotal"]?.toLongOrNull()?.takeIf { it > 0 } ?: return null
        val available = values["memoryAvailable"]?.toLongOrNull()?.takeIf { it in 0..total } ?: return null
        val uptime = values["uptime"]?.toLongOrNull()?.takeIf { it >= 0 } ?: return null
        val disks = output.lineSequence().filter { it.startsWith("disk=") }.mapNotNull {
            val fields = it.removePrefix("disk=").trimEnd().split('\t', limit = 3)
            if (fields.size != 3) return@mapNotNull null
            val size = fields[0].toLongOrNull()?.takeIf { n -> n > 0 } ?: return@mapNotNull null
            val used = fields[1].toLongOrNull()?.takeIf { n -> n in 0..size } ?: return@mapNotNull null
            SshDiskSnapshot(fields[2], used, size)
        }.toList()
        return SshSystemSnapshot(cpu, total - available, total, uptime, values["system"].orEmpty(), disks)
    }

    internal val linuxCommand = """
        export LC_ALL=C
        read _ u n s i w x y z rest < /proc/stat
        t1=${'$'}((u+n+s+i+w+x+y+z)); idle1=${'$'}((i+w)); sleep 1
        read _ u n s i w x y z rest < /proc/stat
        t2=${'$'}((u+n+s+i+w+x+y+z)); idle2=${'$'}((i+w)); delta=${'$'}((t2-t1))
        [ "${'$'}delta" -gt 0 ] || exit 1
        awk -v d="${'$'}delta" -v idle="${'$'}((idle2-idle1))" 'BEGIN {printf "cpu=%.2f\n",100*(d-idle)/d}'
        awk '/^MemTotal:/ {printf "memoryTotal=%.0f\n", ${'$'}2*1024} /^MemAvailable:/ {printf "memoryAvailable=%.0f\n", ${'$'}2*1024}' /proc/meminfo
        printf 'uptime=%s\nsystem=%s\n' "${'$'}(cut -d. -f1 /proc/uptime)" "${'$'}(uname -sr)"
        df -Pk -x tmpfs -x devtmpfs | awk 'NR>1 && ${'$'}2>0 {name=${'$'}6; for(i=7;i<=NF;i++) name=name " " ${'$'}i; printf "disk=%.0f\t%.0f\t%s\n",${'$'}2*1024,${'$'}3*1024,name}'
    """.trimIndent().let { "sh -c '" + it.replace("'", "'\"'\"'") + "'" }

    internal val windowsCommand = """
        ${'$'}ErrorActionPreference='Stop'
        [Console]::OutputEncoding=[Text.Encoding]::UTF8
        ${'$'}os=Get-CimInstance Win32_OperatingSystem
        ${'$'}cpu=(Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average
        'cpu='+${'$'}cpu.ToString([Globalization.CultureInfo]::InvariantCulture)
        'memoryTotal='+([long]${'$'}os.TotalVisibleMemorySize*1024)
        'memoryAvailable='+([long]${'$'}os.FreePhysicalMemory*1024)
        'uptime='+[long]((Get-Date)-${'$'}os.LastBootUpTime).TotalSeconds
        'system='+${'$'}os.Caption+' '+${'$'}os.Version
        Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Where-Object { ${'$'}_.Size -gt 0 } | ForEach-Object { "disk={0}`t{1}`t{2}" -f ${'$'}_.Size,(${ '$'}_.Size-${'$'}_.FreeSpace),${'$'}_.DeviceID }
    """.trimIndent().let {
        "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " +
            Base64.getEncoder().encodeToString(it.toByteArray(Charsets.UTF_16LE))
    }
}
