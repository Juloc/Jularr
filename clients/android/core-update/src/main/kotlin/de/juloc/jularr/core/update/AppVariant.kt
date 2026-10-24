package de.juloc.jularr.core.update

/**
 * Which Jularr Android client is checking for an update. Selects the matching GitHub
 * release asset by the fixed naming convention from docs/ANDROID_CLIENTS.md §13
 * (`Jularr-Mobile-<version>.apk` / `Jularr-TV-<version>.apk`) so a phone can never be
 * offered the TV package or vice versa.
 */
enum class AppVariant(val assetPrefix: String) {
    Mobile("Jularr-Mobile-"),
    Tv("Jularr-TV-"),
}
