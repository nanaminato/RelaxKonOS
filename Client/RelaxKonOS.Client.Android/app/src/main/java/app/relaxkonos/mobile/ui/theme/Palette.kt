package app.relaxkonos.mobile.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

/**
 * The palettes behind the Compose theme.
 *
 * Dynamic colour is deliberately not used: the design requires definite, contrast-checked palettes for
 * light, dark and high contrast so status and danger colours cannot be re-tinted by a device theme.
 * Business screens reference semantic roles only, so a palette change here repaints every screen.
 *
 * Every palette fills the complete Material 3 role set — including the `surfaceContainer*` ramp that
 * the newer Material 3 components (and this app's cards) resolve against. Leaving those at their
 * baseline defaults would drag a purple-grey tint into an otherwise blue palette.
 */

/**
 * Colours that Material 3 has no role for.
 *
 * Success, warning and the decorative gradients are app vocabulary, not Material slots, so they travel
 * through a composition local instead of being smuggled into an unrelated role such as `tertiary`.
 * Import them as `MaterialTheme.relaxKon`.
 */
@Immutable
data class RelaxKonColors(
    val success: Color,
    val onSuccess: Color,
    val successContainer: Color,
    val onSuccessContainer: Color,
    val warning: Color,
    val onWarning: Color,
    val warningContainer: Color,
    val onWarningContainer: Color,
    val info: Color,
    val onInfo: Color,
    val infoContainer: Color,
    val onInfoContainer: Color,
    /** Diagonal gradient of the identity header on the home screen and the sign-in hero. */
    val heroStart: Color,
    val heroEnd: Color,
    val onHero: Color,
    /** Secondary text and icons drawn on top of [heroStart]/[heroEnd]. */
    val onHeroMuted: Color,
    /** Page backdrop gradient, painted underneath every screen. */
    val backdropStart: Color,
    val backdropEnd: Color,
    /** Soft off-screen glows that give the backdrop depth. Keep them faint: content sits on top. */
    val glowPrimary: Color,
    val glowSecondary: Color,
)

private val RelaxKonLightColors = RelaxKonColors(
    success = Color(0xFF15694A),
    onSuccess = Color(0xFFFFFFFF),
    successContainer = Color(0xFFB8F0D2),
    onSuccessContainer = Color(0xFF00210F),
    warning = Color(0xFF8A5A00),
    onWarning = Color(0xFFFFFFFF),
    warningContainer = Color(0xFFFFE2A8),
    onWarningContainer = Color(0xFF2A1800),
    info = Color(0xFF0B5C86),
    onInfo = Color(0xFFFFFFFF),
    infoContainer = Color(0xFFCBE7F7),
    onInfoContainer = Color(0xFF001E2C),
    heroStart = Color(0xFF1B4B87),
    heroEnd = Color(0xFF2E77C6),
    onHero = Color(0xFFFFFFFF),
    onHeroMuted = Color(0xCCFFFFFF),
    backdropStart = Color(0xFFF7F9FE),
    backdropEnd = Color(0xFFE7EEFA),
    glowPrimary = Color(0x1A1F5FA9),
    glowSecondary = Color(0x140F6E63),
)

private val RelaxKonLightHighContrastColors = RelaxKonColors(
    success = Color(0xFF00522F),
    onSuccess = Color(0xFFFFFFFF),
    successContainer = Color(0xFF9FF2C6),
    onSuccessContainer = Color(0xFF000000),
    warning = Color(0xFF6B4400),
    onWarning = Color(0xFFFFFFFF),
    warningContainer = Color(0xFFFFD68C),
    onWarningContainer = Color(0xFF000000),
    info = Color(0xFF00415F),
    onInfo = Color(0xFFFFFFFF),
    infoContainer = Color(0xFFB3DDF4),
    onInfoContainer = Color(0xFF000000),
    heroStart = Color(0xFF0A2F5C),
    heroEnd = Color(0xFF174E86),
    onHero = Color(0xFFFFFFFF),
    onHeroMuted = Color(0xFFFFFFFF),
    backdropStart = Color(0xFFFFFFFF),
    backdropEnd = Color(0xFFEDF1F8),
    glowPrimary = Color(0x1F0B3D75),
    glowSecondary = Color(0x1A0F6E63),
)

private val RelaxKonDarkColors = RelaxKonColors(
    success = Color(0xFF83DBAB),
    onSuccess = Color(0xFF00391F),
    successContainer = Color(0xFF005232),
    onSuccessContainer = Color(0xFF9FF8C4),
    warning = Color(0xFFEFC24C),
    onWarning = Color(0xFF412D00),
    warningContainer = Color(0xFF5E4200),
    onWarningContainer = Color(0xFFFFE2A8),
    info = Color(0xFF92D0F0),
    onInfo = Color(0xFF003548),
    infoContainer = Color(0xFF004D68),
    onInfoContainer = Color(0xFFCBE7F7),
    heroStart = Color(0xFF113862),
    heroEnd = Color(0xFF1E5FA8),
    onHero = Color(0xFFF2F6FF),
    onHeroMuted = Color(0xB3F2F6FF),
    backdropStart = Color(0xFF080C13),
    backdropEnd = Color(0xFF141D2C),
    glowPrimary = Color(0x2E4E8FD8),
    glowSecondary = Color(0x2419A08C),
)

private val RelaxKonDarkHighContrastColors = RelaxKonColors(
    success = Color(0xFFA6FFCB),
    onSuccess = Color(0xFF000000),
    successContainer = Color(0xFF00734A),
    onSuccessContainer = Color(0xFFFFFFFF),
    warning = Color(0xFFFFD98A),
    onWarning = Color(0xFF000000),
    warningContainer = Color(0xFF7A5600),
    onWarningContainer = Color(0xFFFFFFFF),
    info = Color(0xFFC2E8FF),
    onInfo = Color(0xFF000000),
    infoContainer = Color(0xFF016990),
    onInfoContainer = Color(0xFFFFFFFF),
    heroStart = Color(0xFF06172D),
    heroEnd = Color(0xFF0C3C6E),
    onHero = Color(0xFFFFFFFF),
    onHeroMuted = Color(0xFFFFFFFF),
    backdropStart = Color(0xFF000000),
    backdropEnd = Color(0xFF0C1522),
    glowPrimary = Color(0x3D7FB0FF),
    glowSecondary = Color(0x3319A08C),
)

/** Light palette, chosen by [RelaxKonOSTheme] when neither dark nor high contrast applies. */
internal val RelaxKonLight = lightColorScheme(
    primary = Color(0xFF1F5FA9),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFD6E3FF),
    onPrimaryContainer = Color(0xFF001A3C),
    inversePrimary = Color(0xFFA8C8FF),
    secondary = Color(0xFF4A6188),
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFD8E2F9),
    onSecondaryContainer = Color(0xFF041C33),
    tertiary = Color(0xFF0F6E63),
    onTertiary = Color(0xFFFFFFFF),
    tertiaryContainer = Color(0xFFA7F2E3),
    onTertiaryContainer = Color(0xFF00201C),
    background = Color(0xFFF7F9FE),
    onBackground = Color(0xFF1A1C1E),
    surface = Color(0xFFFBFCFE),
    onSurface = Color(0xFF1A1C1E),
    surfaceVariant = Color(0xFFE1E2EC),
    onSurfaceVariant = Color(0xFF43474E),
    surfaceTint = Color(0xFF1F5FA9),
    surfaceBright = Color(0xFFFBFCFE),
    surfaceDim = Color(0xFFDCE2EC),
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFF3F6FC),
    surfaceContainer = Color(0xFFEDF1F8),
    surfaceContainerHigh = Color(0xFFE7ECF5),
    surfaceContainerHighest = Color(0xFFE1E7F1),
    inverseSurface = Color(0xFF2F3033),
    inverseOnSurface = Color(0xFFF1F0F4),
    error = Color(0xFFB3261E),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFF9DEDC),
    onErrorContainer = Color(0xFF410E0B),
    outline = Color(0xFF74777F),
    outlineVariant = Color(0xFFC5C7D0),
    scrim = Color(0xFF000000),
)

/** Light palette with black text on white surfaces and darkened accents, for [ColorMode.Light]. */
internal val RelaxKonLightHighContrast = lightColorScheme(
    primary = Color(0xFF0B3D75),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFBDD6FF),
    onPrimaryContainer = Color(0xFF000000),
    inversePrimary = Color(0xFFCFE0FF),
    secondary = Color(0xFF2A3F61),
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFC6D6F2),
    onSecondaryContainer = Color(0xFF000000),
    tertiary = Color(0xFF00554C),
    onTertiary = Color(0xFFFFFFFF),
    tertiaryContainer = Color(0xFF8FE6D6),
    onTertiaryContainer = Color(0xFF000000),
    background = Color(0xFFFFFFFF),
    onBackground = Color(0xFF000000),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF000000),
    surfaceVariant = Color(0xFFD3D6DE),
    onSurfaceVariant = Color(0xFF101214),
    surfaceTint = Color(0xFF0B3D75),
    surfaceBright = Color(0xFFFFFFFF),
    surfaceDim = Color(0xFFD9DEE8),
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFF6F8FC),
    surfaceContainer = Color(0xFFF0F3F9),
    surfaceContainerHigh = Color(0xFFEAEEF6),
    surfaceContainerHighest = Color(0xFFE4E9F3),
    inverseSurface = Color(0xFF1A1C1E),
    inverseOnSurface = Color(0xFFFFFFFF),
    error = Color(0xFF8C0009),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFFFDAD6),
    onErrorContainer = Color(0xFF000000),
    outline = Color(0xFF2B2F33),
    outlineVariant = Color(0xFF9AA0AA),
    scrim = Color(0xFF000000),
)

/** Dark palette used when [ColorMode.Dark] is chosen or the system is in dark mode. */
internal val RelaxKonDark = darkColorScheme(
    primary = Color(0xFFA8C8FF),
    onPrimary = Color(0xFF00315F),
    primaryContainer = Color(0xFF0B4A8A),
    onPrimaryContainer = Color(0xFFD6E3FF),
    inversePrimary = Color(0xFF1F5FA9),
    secondary = Color(0xFFB6C7E9),
    onSecondary = Color(0xFF1E3149),
    secondaryContainer = Color(0xFF34496B),
    onSecondaryContainer = Color(0xFFD8E2F9),
    tertiary = Color(0xFF89D6C7),
    onTertiary = Color(0xFF003731),
    tertiaryContainer = Color(0xFF0A5248),
    onTertiaryContainer = Color(0xFFA7F2E3),
    background = Color(0xFF0B1018),
    onBackground = Color(0xFFE3E2E6),
    surface = Color(0xFF111A27),
    onSurface = Color(0xFFE3E2E6),
    surfaceVariant = Color(0xFF43474E),
    onSurfaceVariant = Color(0xFFC4C6CF),
    surfaceTint = Color(0xFFA8C8FF),
    surfaceBright = Color(0xFF2B3546),
    surfaceDim = Color(0xFF0B1018),
    surfaceContainerLowest = Color(0xFF060A10),
    surfaceContainerLow = Color(0xFF151D2B),
    surfaceContainer = Color(0xFF19222F),
    surfaceContainerHigh = Color(0xFF1E2836),
    surfaceContainerHighest = Color(0xFF242E3D),
    inverseSurface = Color(0xFFE3E2E6),
    inverseOnSurface = Color(0xFF2F3033),
    error = Color(0xFFFFB4AB),
    onError = Color(0xFF690005),
    errorContainer = Color(0xFF93000A),
    onErrorContainer = Color(0xFFFFDAD6),
    outline = Color(0xFF8E9099),
    outlineVariant = Color(0xFF43474E),
    scrim = Color(0xFF000000),
)

/** Dark palette with maximum separation, for dark mode combined with the high-contrast switch. */
internal val RelaxKonDarkHighContrast = darkColorScheme(
    primary = Color(0xFFCFE0FF),
    onPrimary = Color(0xFF000000),
    primaryContainer = Color(0xFF7FB0FF),
    onPrimaryContainer = Color(0xFF000000),
    inversePrimary = Color(0xFF00315F),
    secondary = Color(0xFFD6E2FF),
    onSecondary = Color(0xFF000000),
    secondaryContainer = Color(0xFF5A74A0),
    onSecondaryContainer = Color(0xFFFFFFFF),
    tertiary = Color(0xFFB6F2E4),
    onTertiary = Color(0xFF000000),
    tertiaryContainer = Color(0xFF2E9483),
    onTertiaryContainer = Color(0xFFFFFFFF),
    background = Color(0xFF000000),
    onBackground = Color(0xFFFFFFFF),
    surface = Color(0xFF0A0E15),
    onSurface = Color(0xFFFFFFFF),
    surfaceVariant = Color(0xFF5A5E67),
    onSurfaceVariant = Color(0xFFFFFFFF),
    surfaceTint = Color(0xFFCFE0FF),
    surfaceBright = Color(0xFF39424F),
    surfaceDim = Color(0xFF000000),
    surfaceContainerLowest = Color(0xFF000000),
    surfaceContainerLow = Color(0xFF10161F),
    surfaceContainer = Color(0xFF151C27),
    surfaceContainerHigh = Color(0xFF1C2430),
    surfaceContainerHighest = Color(0xFF232C39),
    inverseSurface = Color(0xFFFFFFFF),
    inverseOnSurface = Color(0xFF000000),
    error = Color(0xFFFFD2CC),
    onError = Color(0xFF000000),
    errorContainer = Color(0xFFFF897D),
    onErrorContainer = Color(0xFF000000),
    outline = Color(0xFFD7D9E0),
    outlineVariant = Color(0xFF8E9099),
    scrim = Color(0xFF000000),
)

internal val LocalRelaxKonColors = staticCompositionLocalOf { RelaxKonLightColors }

/**
 * App-specific colours for the current theme.
 *
 * Read as `MaterialTheme.relaxKon.success`. Keeping it off `MaterialTheme.colorScheme` means a
 * Material component can never accidentally pick up a status colour for a generic role.
 */
val MaterialTheme.relaxKon: RelaxKonColors
    @Composable
    @ReadOnlyComposable
    get() = LocalRelaxKonColors.current

internal fun lightRelaxKonColors(highContrast: Boolean): RelaxKonColors =
    if (highContrast) RelaxKonLightHighContrastColors else RelaxKonLightColors

internal fun darkRelaxKonColors(highContrast: Boolean): RelaxKonColors =
    if (highContrast) RelaxKonDarkHighContrastColors else RelaxKonDarkColors
