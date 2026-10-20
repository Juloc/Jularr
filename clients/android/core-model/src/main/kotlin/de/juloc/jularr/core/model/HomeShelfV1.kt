package de.juloc.jularr.core.model

/**
 * One shelf of the profile's home page. The order of the list is the order the shelves appear
 * in; the hidden ones carry their flag.
 */
data class HomeShelfV1(
    val homeShelfTypeId: Int,
    val isHidden: Boolean,
) {
    companion object {
        /** The shelf types of the Backend. */
        const val ContinueWatching = 1
        const val ContinueListening = 2
        const val ContinueReading = 3
        const val RecentlyAdded = 4
        const val Recommendations = 5
    }
}
