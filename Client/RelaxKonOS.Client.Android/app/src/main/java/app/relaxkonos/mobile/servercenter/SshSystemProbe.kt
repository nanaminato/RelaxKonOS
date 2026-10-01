package app.relaxkonos.mobile.servercenter

import java.util.Base64

data class SshDiskSnapshot(val name: String, val usedBytes: Long, val totalBytes: Long)

data class SshSystemSnapshot(
    val cpuPercent: Double?,
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
        val snapshot = if (result.succeeded) parse(result.standardOutput) else null
        val stage = result.standardOutput.lineSequence().firstOrNull { it == "problem=memory" || it == "problem=disk" }
            ?.substringAfter('=') ?: "none"
        SshDiagnostics.trace("system.snapshot", "platform=${if (command == linuxCommand) "linux" else "windows"} exit=${result.exitStatus} parsed=${snapshot != null}")
        SshDiagnostics.trace("system.metrics", "cpu=${snapshot?.cpuPercent != null} disks=${snapshot?.disks?.size ?: 0} failed_stage=$stage")
        val errorCode = result.standardOutput.lineSequence().firstOrNull { it.matches(Regex("errorCode=[0-9A-F]{8}")) }
        if (errorCode != null) SshDiagnostics.trace("system.error", errorCode)
        return snapshot
    }

    fun parse(output: String): SshSystemSnapshot? {
        val values = output.lineSequence().mapNotNull {
            it.trim().split('=', limit = 2).takeIf { pair -> pair.size == 2 }
        }.associate { it[0] to it[1] }
        val cpuText = values["cpu"] ?: return null
        val cpu = if (cpuText == "unavailable") null else cpuText.toDoubleOrNull()?.takeIf { it.isFinite() && it in 0.0..100.0 } ?: return null
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
        ${'$'}ProgressPreference='SilentlyContinue'
        [Console]::OutputEncoding=[Text.Encoding]::UTF8
        ${'$'}stage='memory'
        try {
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class HostResources {
            [StructLayout(LayoutKind.Sequential)] public struct Memory {
                public uint Length, Load;
                public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
            }
            [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GlobalMemoryStatusEx(ref Memory value);
            [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
            [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        }
        '@
        ${'$'}memory=New-Object HostResources+Memory
        ${'$'}memory.Length=[Runtime.InteropServices.Marshal]::SizeOf(${'$'}memory)
        if (![HostResources]::GlobalMemoryStatusEx([ref]${'$'}memory)) { throw (New-Object ComponentModel.Win32Exception) }
        [Console]::WriteLine('memoryTotal='+${'$'}memory.TotalPhysical)
        [Console]::WriteLine('memoryAvailable='+${'$'}memory.AvailablePhysical)
        [Console]::WriteLine('uptime='+[long]([HostResources]::GetTickCount64()/1000))
        [Console]::WriteLine('system=Windows '+[Environment]::OSVersion.Version)
        } catch {
            [Console]::WriteLine('problem='+${'$'}stage)
            [Console]::WriteLine('errorCode='+${'$'}_.Exception.HResult.ToString('X8'))
            exit 1
        }
        try {
            [long]${'$'}i1=0; [long]${'$'}k1=0; [long]${'$'}u1=0
            [long]${'$'}i2=0; [long]${'$'}k2=0; [long]${'$'}u2=0
            if (![HostResources]::GetSystemTimes([ref]${'$'}i1,[ref]${'$'}k1,[ref]${'$'}u1)) { throw 'CPU sample failed' }
            Start-Sleep -Seconds 1
            if (![HostResources]::GetSystemTimes([ref]${'$'}i2,[ref]${'$'}k2,[ref]${'$'}u2)) { throw 'CPU sample failed' }
            ${'$'}elapsed=(${'$'}k2-${'$'}k1)+(${'$'}u2-${'$'}u1)
            if (${'$'}elapsed -le 0) { throw 'Invalid CPU sample' }
            ${'$'}cpu=100*(1-((${'$'}i2-${'$'}i1)/[double]${'$'}elapsed))
            [Console]::WriteLine('cpu='+([Math]::Max(0,[Math]::Min(100,${'$'}cpu))).ToString([Globalization.CultureInfo]::InvariantCulture))
        } catch { [Console]::WriteLine('cpu=unavailable') }
        try {
            [IO.DriveInfo]::GetDrives() | Where-Object { ${'$'}_.DriveType -eq 'Fixed' -and ${'$'}_.IsReady } | ForEach-Object { [Console]::WriteLine(("disk={0}`t{1}`t{2}" -f ${'$'}_.TotalSize,(${'$'}_.TotalSize-${'$'}_.TotalFreeSpace),${'$'}_.Name)) }
        } catch { [Console]::WriteLine('problem=disk') }
    """.trimIndent().let {
        "powershell.exe -NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand " +
            Base64.getEncoder().encodeToString(it.toByteArray(Charsets.UTF_16LE))
    }
}
