package app.relaxkonos.mobile.ui.theme

import android.content.Context
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
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
 * The app theme.
 *
 * It publishes three things and nothing else: the Material colour roles, the app-specific colours that
 * Material has no slot for (`MaterialTheme.relaxKon`), and the shape/type scales the layout primitives
 * resolve against. Palettes live in `Palette.kt`; spacing and radii in `Tokens.kt`.
 */
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
    val extras = if (dark) darkRelaxKonColors(highContrast) else lightRelaxKonColors(highContrast)

    CompositionLocalProvider(LocalRelaxKonColors provides extras) {
        MaterialTheme(
            colorScheme = scheme,
            shapes = RelaxKonShapes,
            typography = RelaxKonTypography,
            content = content,
        )
    }
}
