package de.juloc.jularr.tv

/** The order of the titles in the shelves, as the desktop offers it. */
enum class TvSorting(val key: String) {
    Recommended("recommended"),
    Title("az"),
    Year("newest"),
}
