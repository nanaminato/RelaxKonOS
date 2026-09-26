package app.relaxkonos.mobile.servercenter

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** 宿主目标规则的契约测试。与 C# `ServerHostTargetRules` 必须给出同一标识与同一判断。 */
class ServerHostTargetTest {

    private val now = 1_790_000_000_000L
    private val installIdA = "rki-" + "a".repeat(32)
    private val installIdB = "rki-" + "b".repeat(32)

    @Test
    fun `host id is derived from the normalized endpoint`() {
        val target = ServerHostTargetRules.create("Host.Example", 22, "deploy", "  机房 A  ", now)
        assertEquals(ServerHostTargetRules.hostId("host.example", 22), target.hostId)
        assertTrue(ServerHostTargetRules.isHostId(target.hostId))
        assertEquals("host.example", target.sshHost)
        assertEquals("机房 A", target.displayName)
        assertNull(target.installationId)
        assertNull(target.lastVerified)
    }

    @Test
    fun `host id matches the C# derivation byte for byte`() {
        // 这两个值是 C# `ServerHostTargetRules.HostId` 的输出。两端必须逐字一致，
        // 否则同一台宿主会在桌面与手机上得到两条不同的管理资料。
        assertEquals("rkhost-830da5105a935d10", ServerHostTargetRules.hostId("host.example", 22))
        assertEquals("rkhost-3dcd84aa41489796", ServerHostTargetRules.hostId("node-1", 2222))
    }

    @Test
    fun `adding the same endpoint twice does not create a second target`() {
        val first = ServerHostTargetRules.create("host.example", 22, "deploy", null, now)
        val second = ServerHostTargetRules.create("HOST.EXAMPLE", 22, "other", null, now)
        assertEquals(first.hostId, second.hostId)
        assertEquals("host.example:22", second.displayName)
        assertFalse(
            ServerHostTargetRules.hostId("host.example", 22) ==
                ServerHostTargetRules.hostId("host.example", 2222),
        )
    }

    @Test
    fun `invalid endpoint input is rejected`() {
        assertFalse(ServerHostTargetRules.isValidEndpoint("host", 0, "deploy"))
        assertFalse(ServerHostTargetRules.isValidEndpoint("host", 70000, "deploy"))
        assertFalse(ServerHostTargetRules.isValidEndpoint("host", 22, "   "))
        assertTrue(ServerHostTargetRules.isValidEndpoint("host", 22, "deploy"))
    }

    @Test
    fun `installation id only ever comes from a verified result`() {
        val target = ServerHostTargetRules.create("host.example", 22, "deploy", null, now)
        val installed = ServerHostTargetRules.applyVerifiedState(
            target,
            ServerHostVerifiedState(
                installed = true,
                mode = ServerInstallMode.LinuxSystem,
                installationId = installIdA,
                version = "0.1.0",
                listenUrl = "http://127.0.0.1:5090",
                healthy = true,
                verifiedAtEpochMillis = now,
            ),
            now,
        )
        assertTrue(ServerHostTargetRules.hasManagedInstallation(installed))
        assertEquals(installIdA, installed.installationId)
        assertTrue(ServerHostTargetRules.matchesInstallation(installed, installIdA))
        assertFalse(ServerHostTargetRules.matchesInstallation(installed, installIdB))
        // 相同 IP 或 URL 文本都不足以把宿主与登录合并。
        assertFalse(ServerHostTargetRules.matchesInstallation(installed, "http://host.example"))
    }

    @Test
    fun `a not-installed result does not clear the known installation id`() {
        val target = ServerHostTargetRules.create("host.example", 22, "deploy", null, now)
            .copy(installationId = installIdA)
        val uninstalled = ServerHostTargetRules.applyVerifiedState(
            target,
            ServerHostVerifiedState(false, null, null, null, null, false, now + 1),
            now + 1,
        )
        assertEquals(installIdA, uninstalled.installationId)
        assertEquals(false, uninstalled.lastVerified?.installed)
    }

    @Test
    fun `an invalid installation id is never adopted`() {
        val target = ServerHostTargetRules.create("host.example", 22, "deploy", null, now)
        val result = ServerHostTargetRules.applyVerifiedState(
            target,
            ServerHostVerifiedState(true, ServerInstallMode.LinuxUser, "not-an-id", "0.1.0", null, true, now),
            now,
        )
        assertNull(result.installationId)
    }

    @Test
    fun `host key trust outranks deployment state`() {
        val target = ServerHostTargetRules.create("host.example", 22, "deploy", null, now)
            .copy(
                installationId = installIdA,
                lastVerified = ServerHostVerifiedState(true, ServerInstallMode.LinuxSystem, installIdA, "0.1.0", null, true, now),
            )
        assertEquals(
            ServerHostTargetStatus.HostKeyChanged,
            ServerHostTargetRules.status(target, ServerHostKeyTrust.Changed),
        )
        assertEquals(
            ServerHostTargetStatus.HostKeyUnknown,
            ServerHostTargetRules.status(target, ServerHostKeyTrust.Unknown),
        )
        assertEquals(
            ServerHostTargetStatus.InstalledHealthy,
            ServerHostTargetRules.status(target, ServerHostKeyTrust.Trusted),
        )
        assertEquals(
            ServerHostTargetStatus.Unverified,
            ServerHostTargetRules.status(
                ServerHostTargetRules.create("other.example", 22, "deploy", null, now),
                ServerHostKeyTrust.Trusted,
            ),
        )
        assertEquals(
            ServerHostTargetStatus.ReachableNotInstalled,
            ServerHostTargetRules.status(
                target.copy(
                    lastVerified = ServerHostVerifiedState(false, null, null, null, null, false, now),
                ),
                ServerHostKeyTrust.Trusted,
            ),
        )
        assertEquals(
            ServerHostTargetStatus.ServiceUnhealthy,
            ServerHostTargetRules.status(
                target.copy(
                    lastVerified = ServerHostVerifiedState(true, ServerInstallMode.LinuxSystem, installIdA, "0.1.0", null, false, now),
                ),
                ServerHostKeyTrust.Trusted,
            ),
        )
        assertNotNull(ServerHostTargetRules.status(target, ServerHostKeyTrust.Trusted))
    }
}