package app.relaxkonos.mobile.ui.theme

import android.content.Context
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.core.os.LocaleListCompat

/** Colour mode offered in appearance settings. The explicit choice always beats the system value. */
enum class ColorMode { FollowSystem, Light, Dark }

/**
 * Languages the app ships, matching `app/src/main/res/values*` and `res/xml/locales_config.xml`.
 * [FollowSystem] is stored as an empty tag and resolved by the platform.
 */
enum class AppLanguage(val tag: String) {
    FollowSystem(""),
    English("en"),
    SimplifiedChinese("zh-CN"),
    Japanese("ja");

    companion object {
        /** Accepts both the BCP-47 form the platform uses and the legacy `zh-rCN` resource form. */
        fun fromStoredTag(tag: String): AppLanguage = when {
            tag.isBlank() -> FollowSystem
            tag.startsWith("zh") -> SimplifiedChinese
            tag.startsWith("ja") -> Japanese
            tag.startsWith("en") -> English
            else -> FollowSystem
        }
    }
}

/**
 * Client-side appearance and fingerprint preferences.
 *
 * These are experience settings, not credentials, so they live in `SharedPreferences` and never hold
 * a secret. Anything security-relevant (passwords, whether a vault is usable) is derived from the
 * Keystore state instead.
 */
class AppearancePreferences(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    var colorMode: ColorMode
        get() = runCatching { ColorMode.valueOf(preferences.getString(KEY_COLOR_MODE, null) ?: ColorMode.FollowSystem.name) }
            .getOrDefault(ColorMode.FollowSystem)
        set(value) {
            preferences.edit().putString(KEY_COLOR_MODE, value.name).apply()
        }

    var highContrast: Boolean
        get() = preferences.getBoolean(KEY_HIGH_CONTRAST, false)
        set(value) {
            preferences.edit().putBoolean(KEY_HIGH_CONTRAST, value).apply()
        }

    /** Empty means "follow the system language". */
    var languageTag: String
        get() = preferences.getString(KEY_LANGUAGE, "").orEmpty()
        set(value) {
            preferences.edit().putString(KEY_LANGUAGE, value).apply()
        }

    /** Master switch for the fingerprint vaults. Off means no password is stored at all. */
    var fingerprintEnabled: Boolean
        get() = preferences.getBoolean(KEY_FINGERPRINT, true)
        set(value) {
            preferences.edit().putBoolean(KEY_FINGERPRINT, value).apply()
        }

    private companion object {
        const val FILE = "relaxkonos.appearance"
        const val KEY_COLOR_MODE = "colorMode"
        const val KEY_HIGH_CONTRAST = "highContrast"
        const val KEY_LANGUAGE = "language"
        const val KEY_FINGERPRINT = "fingerprintEnabled"
    }
}

/**
 * Compose-observable view of [AppearancePreferences].
 *
 * `SharedPreferences` is not observable, so the shell reads settings through this holder. Every write
 * both persists and updates the state, which is what lets a colour-mode change repaint immediately
 * instead of after a restart. Vault usability is deliberately *not* cached here: it is recomputed from
 * the live Keystore probe so turning the master switch off can never leave a stale "usable" answer.
 */
class AppearanceState(private val preferences: AppearancePreferences) {
    private var colorModeState by mutableStateOf(preferences.colorMode)

    private var highContrastState by mutableStateOf(preferences.highContrast)

    private var languageState by mutableStateOf(AppLanguage.fromStoredTag(preferences.languageTag))

    private var fingerprintEnabledState by mutableStateOf(preferences.fingerprintEnabled)

    /**
     * The four settings are read-only views; every write goes through a named action below.
     *
     * Each write has a side effect outside Compose — persisting to `SharedPreferences`, and for the
     * master switch also dropping both vaults — so a bare assignment would hide real work behind
     * property syntax. The names could not be generated setters either: a `var` declared next to an
     * explicit `setX` emits two JVM `setX` methods and does not compile.
     */
    val colorMode: ColorMode get() = colorModeState

    val highContrast: Boolean get() = highContrastState

    val language: AppLanguage get() = languageState

    val fingerprintEnabled: Boolean get() = fingerprintEnabledState

    fun setColorMode(value: ColorMode) {
        preferences.colorMode = value
        colorModeState = value
    }

    fun setHighContrast(value: Boolean) {
        preferences.highContrast = value
        highContrastState = value
    }

    fun setLanguage(value: AppLanguage) {
        preferences.languageTag = value.tag
        languageState = value
    }

    fun setFingerprintEnabled(value: Boolean) {
        preferences.fingerprintEnabled = value
        fingerprintEnabledState = value
    }
}

/**
 * Pushes the stored language into AppCompat, which is the only per-app locale mechanism that also
 * works below API 33. Called from `MainActivity.onCreate` before `super.onCreate`: on older platforms
 * AppCompat resolves locales while attaching the base context, so applying it later would need a
 * second recreation.
 */
fun applyAppLanguage(language: AppLanguage) {
    val requested = if (language.tag.isEmpty()) LocaleListCompat.getEmptyLocaleList() else LocaleListCompat.forLanguageTags(language.tag)
    if (AppCompatDelegate.getApplicationLocales() != requested) {
        AppCompatDelegate.setApplicationLocales(requested)
    }
}

/**
 * Keeps the XML window frame (see `res/values/themes.xml`) aligned with the Compose colour mode, so a
 * forced light theme does not sit on a dark window background.
 */
fun applyAppNightMode(colorMode: ColorMode) {
    val mode = when (colorMode) {
        ColorMode.FollowSystem -> AppCompatDelegate.MODE_NIGHT_FOLLOW_SYSTEM
        ColorMode.Light -> AppCompatDelegate.MODE_NIGHT_NO
        ColorMode.Dark -> AppCompatDelegate.MODE_NIGHT_YES
    }
    if (AppCompatDelegate.getDefaultNightMode() != mode) {
        AppCompatDelegate.setDefaultNightMode(mode)
    }
}

/**
 * Fixed RelaxKonOS palettes.
 *
 * Dynamic colour is intentionally not used: the design requires definite, contrast-checked palettes
 * for light, dark and high contrast so status and danger colours cannot be re-tinted by a device
 * theme. Business screens reference semantic roles only.
 */
private val RelaxKonLight = lightColorScheme(
    primary = Color(0xFF1F5FA9),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFD4E3FF),
    onPrimaryContainer = Color(0xFF001B3D),
    secondary = Color(0xFF475F87),
    onSecondary = Color(0xFFFFFFFF),
    surface = Color(0xFFFAF9FD),
    onSurface = Color(0xFF1A1C1E),
    surfaceVariant = Color(0xFFE0E2EC),
    onSurfaceVariant = Color(0xFF43474E),
    background = Color(0xFFFAF9FD),
    onBackground = Color(0xFF1A1C1E),
    outline = Color(0xFF74777F),
    error = Color(0xFFB3261E),
    onError = Color(0xFFFFFFFF),
)

private val RelaxKonLightHighContrast = lightColorScheme(
    primary = Color(0xFF0B3D75),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFBDD6FF),
    onPrimaryContainer = Color(0xFF000000),
    secondary = Color(0xFF2A3F61),
    onSecondary = Color(0xFFFFFFFF),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF000000),
    surfaceVariant = Color(0xFFD3D6DE),
    onSurfaceVariant = Color(0xFF101214),
    background = Color(0xFFFFFFFF),
    onBackground = Color(0xFF000000),
    outline = Color(0xFF2B2F33),
    error = Color(0xFF8C0009),
    onError = Color(0xFFFFFFFF),
)

private val RelaxKonDark = darkColorScheme(
    primary = Color(0xFFA8C8FF),
    onPrimary = Color(0xFF00315F),
    primaryContainer = Color(0xFF004786),
    onPrimaryContainer = Color(0xFFD4E3FF),
    secondary = Color(0xFFB6C7E9),
    onSecondary = Color(0xFF1E3149),
    surface = Color(0xFF1B2433),
    onSurface = Color(0xFFE2E2E6),
    surfaceVariant = Color(0xFF44474E),
    onSurfaceVariant = Color(0xFFC4C6CF),
    background = Color(0xFF10151F),
    onBackground = Color(0xFFE2E2E6),
    outline = Color(0xFF8E9099),
    error = Color(0xFFFFB4AB),
    onError = Color(0xFF690005),
)

private val RelaxKonDarkHighContrast = darkColorScheme(
    primary = Color(0xFFCFE0FF),
    onPrimary = Color(0xFF000000),
    primaryContainer = Color(0xFF7FB0FF),
    onPrimaryContainer = Color(0xFF000000),
    secondary = Color(0xFFD6E2FF),
    onSecondary = Color(0xFF000000),
    surface = Color(0xFF10151F),
    onSurface = Color(0xFFFFFFFF),
    surfaceVariant = Color(0xFF5A5E67),
    onSurfaceVariant = Color(0xFFFFFFFF),
    background = Color(0xFF0A0E15),
    onBackground = Color(0xFFFFFFFF),
    outline = Color(0xFFD7D9E0),
    error = Color(0xFFFFD2CC),
    onError = Color(0xFF000000),
)

@Composable
fun RelaxKonOSTheme(
    colorMode: ColorMode,
    highContrast: Boolean,
    content: @Composable () -> Unit,
) {
    val dark = when (colorMode) {
        ColorMode.FollowSystem -> isSystemInDarkTheme()
        ColorMode.Light -> false
        ColorMode.Dark -> true
    }
    val scheme = when {
        dark && highContrast -> RelaxKonDarkHighContrast
        dark -> RelaxKonDark
        highContrast -> RelaxKonLightHighContrast
        else -> RelaxKonLight
    }
    MaterialTheme(colorScheme = scheme, content = content)
}
