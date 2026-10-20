package de.juloc.jularr.tv

import de.juloc.jularr.core.model.ContinueItemV1
import de.juloc.jularr.core.model.WorkCardV1
import kotlin.math.roundToInt

/**
 * Reads the cards of the server. The client shows the wording of the instance: the state of
 * a work and the unit of a title come from the texts of the server.
 */
fun WorkCardV1.toTvCard(texts: TvTexts): TvCard = TvCard(
    id = "work-$id",
    title = title,
    posterUrl = posterUrl,
    meta = texts["media.state." + mediaStateName(mediaStateTypeId)],
    progress = null,
    availabilityStateId = availabilityStateId,
)

/** Reads a title the profile continues: its unit and how far it is watched or read. */
fun ContinueItemV1.toTvCard(texts: TvTexts): TvCard = TvCard(
    id = "asset-$mediaAssetId",
    title = title,
    posterUrl = posterUrl,
    meta = unitLabel(texts),
    progress = progression?.toFloat() ?: position(durationMs),
    availabilityStateId = null,
)

/** The name of the state of a work, as the texts of the server know it. */
private fun mediaStateName(mediaStateTypeId: Int): String = when (mediaStateTypeId) {
    1 -> "NotRequested"
    2 -> "Requested"
    3 -> "Available"
    else -> "NotRequested"
}

/** The unit of a title: the season and the episode, the volume and the chapter. */
private fun ContinueItemV1.unitLabel(texts: TvTexts): String? {
    val unit = workUnitTypeId ?: return null
    val number = unitNumber ?: return null
    val label = texts["library.unit." + unitName(unit)] + " " + number.label()
    val parent = parentUnitNumber
    val parentType = when (unit) {
        2 -> 1
        4 -> 3
        else -> null
    }
    return if (parent != null && parentType != null) {
        texts["library.unit." + unitName(parentType)] + " " + parent.label() + " · " + label
    } else {
        label
    }
}

/** The name of a unit type, as the texts of the server know it. */
private fun unitName(workUnitTypeId: Int): String = when (workUnitTypeId) {
    1 -> "Season"
    2 -> "Episode"
    3 -> "Volume"
    4 -> "Chapter"
    5 -> "Track"
    else -> "None"
}

/** A number of the server without its trailing zeros. */
private fun Double.label(): String = if (this == this.roundToInt().toDouble()) {
    this.roundToInt().toString()
} else {
    this.toString()
}

/** How far a title is watched or read. */
private fun ContinueItemV1.position(durationMs: Long?): Float? {
    val duration = durationMs ?: return null
    if (duration <= 0) return null
    return (positionMs.toDouble() / duration).coerceIn(0.0, 1.0).toFloat()
}
