package app.relaxkonos.mobile.ui.common

import app.relaxkonos.mobile.data.InMemoryNoticeStorage
import app.relaxkonos.mobile.data.NoticePreferenceStore
import app.relaxkonos.mobile.data.ReminderKind
import app.relaxkonos.mobile.security.UnlockFailure
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * 「哪一条解锁结论可以被回答掉」是产品规则，不是渲染细节：提权对话框无法像浮动提示那样整段跳过
 * 已静音的消息，所以这条判定必须留在调用点，并且必须能被单独验证（`Shell.Design.md` §3.4 / §5.4）。
 */
class SilenceableUnlockVerdictTest {
    private fun preference() = NoticePreferenceStore(InMemoryNoticeStorage())

    @Test fun `an unavailable device offers the shared unlock reminder`() {
        assertEquals(
            ReminderKind.SavedPasswordUnavailable,
            silenceableUnlockVerdict(UnlockFailure.Unavailable, preference()),
        )
    }

    @Test fun `an answered device stops being asked`() {
        val preference = preference()
        preference.silence(ReminderKind.SavedPasswordUnavailable)
        assertNull(silenceableUnlockVerdict(UnlockFailure.Unavailable, preference))
    }

    @Test fun `silencing another reminder leaves this question open`() {
        val preference = preference()
        preference.silence(ReminderKind.LoginCredentialNotSavedDevice)
        assertEquals(
            ReminderKind.SavedPasswordUnavailable,
            silenceableUnlockVerdict(UnlockFailure.Unavailable, preference),
        )
    }

    /** A new [UnlockFailure] must be classified on purpose, not inherit an offer by accident. */
    @Test fun `exactly one unlock failure is silenceable`() {
        val silenceable = UnlockFailure.entries.filter { it != UnlockFailure.Unavailable }
        silenceable.forEach { failure -> assertNull(failure.name, silenceableUnlockVerdict(failure, preference())) }
        assertEquals(
            "only Unavailable may be answered for good",
            listOf(ReminderKind.SavedPasswordUnavailable),
            UnlockFailure.entries.mapNotNull { silenceableUnlockVerdict(it, preference()) },
        )
    }
}
