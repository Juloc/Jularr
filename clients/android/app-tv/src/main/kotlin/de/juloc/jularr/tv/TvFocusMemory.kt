package de.juloc.jularr.tv

/**
 * Stores the most recently focused item for each active TV screen.
 * Kept across Compose screen recreation by the V1 catalog host, scoped to a server/session.
 *
 * The host and each screen agree on stable, explicit keys such as "home" and "search".
 * This map does not itself move the Android D-pad focus; physical focus restoration needs
 * separate Compose and device validation.
 */
class TvFocusMemory {
    private val focusedItemIds = mutableMapOf<String, String>()

    fun remember(
        screenKey: String,
        itemId: String,
    ) {
        focusedItemIds[screenKey] = itemId
    }

    fun recall(screenKey: String): String? = focusedItemIds[screenKey]

    fun forget(screenKey: String) {
        focusedItemIds.remove(screenKey)
    }
}
