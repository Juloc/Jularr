package de.juloc.jularr.tv

/**
 * Remembers which item was last focused on each TV screen (docs/ANDROID_CLIENTS.md §9,
 * #522: "remember the last focused item per screen"), keyed by [TvNavigation.screenKey].
 *
 * This is deliberately a plain in-memory map rather than Compose `rememberSaveable` state:
 * screens are recreated as [de.juloc.jularr.tv.TvRoute] changes, so per-composable
 * remembered state does not survive navigating away and back. A single [TvFocusMemory]
 * instance is held for the lifetime of [TvAppHost] and threaded through every screen.
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
