package de.juloc.jularr.tv

enum class TvContentFilter(val labelRes: Int) {
    ALL(R.string.tv_home_filter_all),
    ANIME(R.string.tv_home_filter_anime),
    SERIES(R.string.tv_home_filter_series),
    MOVIES(R.string.tv_home_filter_movies),
    ;

    fun includes(animeFormat: String?): Boolean {
        val movie = animeFormat?.equals("MOVIE", ignoreCase = true) == true
        return when (this) {
            ALL, ANIME -> true
            SERIES -> !movie
            MOVIES -> movie
        }
    }

    companion object {
        val visible: List<TvContentFilter> = listOf(
            ALL,
            ANIME,
            SERIES,
            MOVIES,
        )
    }
}
