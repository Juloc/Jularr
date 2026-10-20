package de.juloc.jularr.core.model

/**
 * One title of a card list. The card carries the canonical availability of the title, the real
 * languages of its tracks and the state of its request; the client renders what the server
 * says and decides nothing itself.
 */
data class WorkCardV1(
    val id: Long,
    val mediaTypeId: Int,
    val mediaSubtypeId: Int?,
    val title: String,
    val releaseYear: Int?,
    val posterUrl: String?,
    val mediaStateTypeId: Int,
    val isWatchlisted: Boolean,
    val availabilityStateId: Int,
    val availableUnits: Int,
    val monitoredUnits: Int,
    val audioLanguages: List<String>,
    val subtitleLanguages: List<String>,
) {
    companion object {
        /** The canonical media availability states of the Backend. */
        const val AvailabilityAvailable = 1
        const val AvailabilityPartial = 2
        const val AvailabilityMissing = 3
        const val AvailabilityDownloading = 4
        const val AvailabilitySearching = 5
        const val AvailabilityRequested = 6
        const val AvailabilityProfileAudio = 7
        const val AvailabilityOtherAudio = 8
        const val AvailabilityPartialOtherAudio = 9
    }
}
