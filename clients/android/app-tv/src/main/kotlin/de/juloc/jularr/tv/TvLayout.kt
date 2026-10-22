package de.juloc.jularr.tv

import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * The layout of the TV in the scale of the layout specification. The specification counts the
 * pixels of a 1080p screen, where two pixels are one dp; the TV shows the desktop's sizes
 * grown by the factor of the scale.
 */
object TvLayout {
    /** The reference screen of the specification: 1080p shows two pixels per dp. */
    const val PixelsPerDp = 2f

    /** The factor of the TV scale: the desktop's sizes grow by a third. */
    const val Scale = 1.45f

    /** The card of the desktop is 230 pixels wide; the TV shows it with 340 pixels. */
    val CardWidth = 170.dp
    val CardHeight = 255.dp
    val CardRadius = 16.dp

    /** The hero of the desktop is 260 pixels high; the TV shows it with 340 pixels. */
    val HeroHeight = 170.dp
    val HeroRadius = 10.dp

    /** The rail of the desktop is 220 pixels wide. */
    val RailWidth = 160.dp
    val RailItemRadius = 999.dp

    /** The top row of the desktop is 64 pixels high; the TV shows it with 92 pixels. */
    val TopRowHeight = 46.dp

    /** One entry of the rail is 44 pixels high; the TV shows it with 64 pixels. */
    val RailItemHeight = 32.dp

    /** The symbol of the availability is 32 pixels; the TV shows it with 46 pixels. */
    val AvailabilitySymbol = 23.dp

    /** The top row of the desktop is a panel with a radius of 24 pixels. */
    val TopRowRadius = 17.5.dp

    /** A control of the desktop has the radius of a pill. */
    val ControlRadius = 999.dp

    /** The distance between the shelves, the cards and the sections. */
    val Gap = 8.dp
    val ContentPadding = 17.5.dp

    /** The focus ring of the desktop is two pixels wide; the TV shows it thicker. */
    val FocusRing = 1.5.dp

    /** The size of a desktop size on the television. */
    fun dp(desktopPixels: Float): Dp = (desktopPixels * Scale / PixelsPerDp).dp
}
