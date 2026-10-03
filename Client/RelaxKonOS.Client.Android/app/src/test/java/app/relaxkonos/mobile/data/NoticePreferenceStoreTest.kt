package app.relaxkonos.mobile.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 「不再提醒」偏好的三条性质：
 *
 * - 静音是**按本机、按键**记录的，一条静音不影响另一条；
 * - 它是可逆的，恢复后不再记着；
 * - 版本更替写入的未知键不能被顺手清掉——那会把用户在其他版本里的选择悄悄抹掉。
 */
class NoticePreferenceStoreTest {
    private val storage = InMemoryNoticeStorage()
    private val store = NoticePreferenceStore(storage)

    @Test
    fun `nothing is silenced by default`() {
        assertTrue(store.silenced().isEmpty())
        ReminderKind.entries.forEach { assertFalse(store.isSilenced(it)) }
    }

    @Test
    fun `silencing one reminder leaves the others alone`() {
        store.setSilenced(ReminderKind.SavedPasswordUnavailable, true)

        assertTrue(store.isSilenced(ReminderKind.SavedPasswordUnavailable))
        assertFalse(store.isSilenced(ReminderKind.LoginCredentialNotSavedDevice))
        assertFalse(store.isSilenced(ReminderKind.ServerCenterCredentialNotSaved))
        assertEquals(listOf(ReminderKind.SavedPasswordUnavailable), store.silenced())
    }

    @Test
    fun `silencing survives a new store over the same storage`() {
        store.setSilenced(ReminderKind.LoginCredentialNotSavedDevice, true)

        assertTrue(NoticePreferenceStore(storage).isSilenced(ReminderKind.LoginCredentialNotSavedDevice))
    }

    @Test
    fun `a reminder can be brought back`() {
        store.setSilenced(ReminderKind.LoginCredentialNotSavedDevice, true)
        store.setSilenced(ReminderKind.LoginCredentialNotSavedDevice, false)

        assertFalse(store.isSilenced(ReminderKind.LoginCredentialNotSavedDevice))
        assertTrue(store.silenced().isEmpty())
    }

    @Test
    fun `silenced list follows the declaration order`() {
        store.setSilenced(ReminderKind.ServerCenterCredentialNotSaved, true)
        store.setSilenced(ReminderKind.LoginCredentialNotSavedDevice, true)

        assertEquals(
            listOf(ReminderKind.LoginCredentialNotSavedDevice, ReminderKind.ServerCenterCredentialNotSaved),
            store.silenced(),
        )
    }

    @Test
    fun `keys written by another version are preserved`() {
        storage.write(setOf("some.future-reminder"))

        store.setSilenced(ReminderKind.SavedPasswordUnavailable, true)
        store.setSilenced(ReminderKind.SavedPasswordUnavailable, false)

        assertEquals(setOf("some.future-reminder"), storage.read())
    }

    @Test
    fun `every kind owns a distinct storage key`() {
        val keys = ReminderKind.entries.map { it.storageKey }

        assertEquals(keys.size, keys.toSet().size)
        assertTrue(keys.all { it.isNotBlank() })
    }
}
