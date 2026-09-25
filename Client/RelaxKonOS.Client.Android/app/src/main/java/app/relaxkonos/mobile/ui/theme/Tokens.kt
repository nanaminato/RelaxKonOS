package app.relaxkonos.mobile.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * Spacing scale.
 *
 * One scale for the whole app so vertical rhythm does not drift page by page. Screens should compose
 * these instead of writing raw `dp` literals at the call site.
 */
object Spacing {
    /** 4dp — inside a chip or between an icon and its label. */
    val xs = 4.dp

    /** 8dp — between tightly related lines. */
    val sm = 8.dp

    /** 12dp — between rows inside a card. */
    val md = 12.dp

    /** 16dp — card padding and the page gutter. */
    val lg = 16.dp

    /** 24dp — between sections, and the horizontal gutter on tablets. */
    val xl = 24.dp

    /** 32dp — around a hero header. */
    val xxl = 32.dp
}

/** Corner radii. Larger, softer radii are what separates the current look from the flat original. */
object Radius {
    /** 10dp — chips, badges, small inner surfaces. */
    val sm = 10.dp

    /** 14dp — buttons and text fields. */
    val md = 14.dp

    /** 20dp — cards. */
    val lg = 20.dp

    /** 28dp — hero panels and sheets. */
    val xl = 28.dp

    /** Fully rounded, for pills. */
    val pill = 999.dp
}

/** Sizes shared by the layout primitives, so a header and a card never disagree about width. */
object Layout {
    /** Widest a centred column may get before it stops growing; keeps tablets readable. */
    val contentMaxWidth = 720.dp

    /** Widest a single card column may get in a two-pane layout. */
    val paneMaxWidth = 560.dp

    /** Height of the small icon badge that leads a card header or a list row. */
    val iconBadge = 40.dp

    /**
     * Height of the image preview in the file detail pane.
     *
     * A fixed height rather than an aspect ratio computed from the decoded picture: the box has to
     * exist before the picture does, and it has to measure the same while it is empty, while it is
     * loading and once it is full — otherwise the properties below it jump around as the image arrives.
     */
    val previewHeight = 220.dp
}

/**
 * Shape scale.
 *
 * `medium` is what buttons and text fields resolve against, so it carries the 14dp radius; cards and
 * hero panels use `large`/`extraLarge`.
 */
internal val RelaxKonShapes = Shapes(
    extraSmall = RoundedCornerShape(Radius.sm),
    small = RoundedCornerShape(Radius.md),
    medium = RoundedCornerShape(Radius.md),
    large = RoundedCornerShape(Radius.lg),
    extraLarge = RoundedCornerShape(Radius.xl),
)

/**
 * Type scale.
 *
 * Only the styles the app actually differentiates are overridden: headings get a slightly heavier
 * weight and tighter tracking, and the body styles get a comfortable line height. Sizes that Material
 * already sizes well are left alone so components keep their expected proportions.
 */
internal val RelaxKonTypography = Typography().let { base ->
    base.copy(
        headlineMedium = base.headlineMedium.copy(
            fontWeight = FontWeight.SemiBold,
            letterSpacing = (-0.25).sp,
        ),
        headlineSmall = base.headlineSmall.copy(
            fontWeight = FontWeight.SemiBold,
            letterSpacing = (-0.2).sp,
        ),
        titleLarge = base.titleLarge.copy(fontWeight = FontWeight.SemiBold),
        titleMedium = base.titleMedium.copy(fontWeight = FontWeight.SemiBold),
        titleSmall = base.titleSmall.copy(fontWeight = FontWeight.SemiBold),
        labelLarge = base.labelLarge.copy(fontWeight = FontWeight.SemiBold),
        bodyMedium = base.bodyMedium.copy(lineHeight = 21.sp),
        bodySmall = base.bodySmall.copy(lineHeight = 18.sp),
    )
}

/** Title style used by the page header of every destination. */
internal val ScreenTitleStyle: TextStyle
    get() = RelaxKonTypography.headlineSmall
