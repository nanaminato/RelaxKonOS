package app.relaxkonos.mobile.servercenter

import app.relaxkonos.mobile.core.auth.CredentialGap
import app.relaxkonos.mobile.core.auth.SavedCredentialState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 点开主机与保存密码两个决策的契约测试。
 *
 * 这里覆盖的是「界面不许自己发明结论」的部分：四种保存状态各自映射到哪一个动作、失败原因
 * 是否被如实区分、以及什么条件下才允许写入保险箱。
 */
class SshHostOpenRulesTest {

    @Test
    fun `a usable saved credential is what makes a tap connect without asking`() {
        val plan = planSshHostOpen(SavedCredentialState.Available)
        assertEquals(SshHostOpenAction.ConnectWithSavedPassword, plan.action)
        assertNull(plan.gap)
    }

    @Test
    fun `every unusable credential state asks for the password`() {
        for (state in listOf(
            SavedCredentialState.Absent,
            SavedCredentialState.Unavailable,
            SavedCredentialState.Invalidated,
        )) {
            assertEquals(SshHostOpenAction.AskForPassword, planSshHostOpen(state).action)
        }
    }

    /** 三种「要输入密码」的原因不能共用一句文案，因此映射必须两两不同且都不为空。 */
    @Test
    fun `the three ask-for-password reasons stay distinguishable`() {
        assertEquals(
            CredentialGap.Absent,
            planSshHostOpen(SavedCredentialState.Absent).gap,
        )
        assertEquals(
            CredentialGap.Unavailable,
            planSshHostOpen(SavedCredentialState.Unavailable).gap,
        )
        assertEquals(
            CredentialGap.Invalidated,
            planSshHostOpen(SavedCredentialState.Invalidated).gap,
        )
    }

    @Test
    fun `a saved password is only written when the user asked and the device can protect it`() {
        assertTrue(shouldSaveSshPassword(rememberRequested = true, canProtectOnThisDevice = true, origin = SshPasswordOrigin.Typed))
        assertFalse(shouldSaveSshPassword(rememberRequested = false, canProtectOnThisDevice = true, origin = SshPasswordOrigin.Typed))
        assertFalse(shouldSaveSshPassword(rememberRequested = true, canProtectOnThisDevice = false, origin = SshPasswordOrigin.Typed))
        assertFalse(shouldSaveSshPassword(rememberRequested = false, canProtectOnThisDevice = false, origin = SshPasswordOrigin.Typed))
    }

    /**
     * 解封得来的密码本来就保存着，本会话内存里的副本也来自一次已经问过的验证：两者都不能再弹一次
     * 加密确认，否则同一次指纹授权会做两遍（解封一遍、加密一遍）。
     */
    @Test
    fun `a password that came from the vault or from this session is never offered again`() {
        for (origin in listOf(SshPasswordOrigin.UnsealedFromVault, SshPasswordOrigin.SessionMemory)) {
            assertFalse(shouldSaveSshPassword(rememberRequested = true, canProtectOnThisDevice = true, origin = origin))
        }
    }

    /** 将来新增一个来源必须显式决定它算不算一次新的保存，而不是默默沿用旧结论。 */
    @Test
    fun `exactly one origin is worth asking to save`() {
        val asking = SshPasswordOrigin.entries.filter {
            shouldSaveSshPassword(rememberRequested = true, canProtectOnThisDevice = true, origin = it)
        }
        assertEquals(listOf(SshPasswordOrigin.Typed), asking)
    }
}
