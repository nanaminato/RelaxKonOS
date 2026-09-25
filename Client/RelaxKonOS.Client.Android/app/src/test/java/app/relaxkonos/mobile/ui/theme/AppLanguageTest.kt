package app.relaxkonos.mobile.ui.theme

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Stored-language folding.
 *
 * Only one Chinese translation ships, so every `zh` variant the platform can report has to land on
 * it; a Traditional-script device must not fall through to English. Anything this build does not ship
 * follows the system, which the resource folders then resolve to English.
 */
class AppLanguageTest {
    @Test
    fun `a blank tag means follow the system`() {
        assertEquals(AppLanguage.FollowSystem, AppLanguage.fromStoredTag(""))
    }

    @Test
    fun `every chinese variant folds onto one language`() {
        val tags = listOf("zh", "zh-CN", "zh-Hans-CN", "zh-TW", "zh-Hant-TW", "zh-HK", "zh-SG", "ZH")

        tags.forEach { tag ->
            assertEquals(tag, AppLanguage.Chinese, AppLanguage.fromStoredTag(tag))
        }
    }

    @Test
    fun `japanese keeps its own language`() {
        assertEquals(AppLanguage.Japanese, AppLanguage.fromStoredTag("ja"))
        assertEquals(AppLanguage.Japanese, AppLanguage.fromStoredTag("ja-JP"))
    }

    @Test
    fun `english keeps its own language`() {
        assertEquals(AppLanguage.English, AppLanguage.fromStoredTag("en"))
        assertEquals(AppLanguage.English, AppLanguage.fromStoredTag("en-GB"))
    }

    @Test
    fun `a language this build does not ship follows the system`() {
        listOf("fr", "de-DE", "ko", "ar-EG").forEach { tag ->
            assertEquals(tag, AppLanguage.FollowSystem, AppLanguage.fromStoredTag(tag))
        }
    }

    @Test
    fun `every shipped language round-trips through its stored tag`() {
        AppLanguage.entries.forEach { language ->
            assertEquals(language, AppLanguage.fromStoredTag(language.tag))
        }
    }
}
