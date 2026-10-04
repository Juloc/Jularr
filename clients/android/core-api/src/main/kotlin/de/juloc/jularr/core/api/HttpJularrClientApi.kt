package de.juloc.jularr.core.api

import de.juloc.jularr.core.model.AnimeDetail
import de.juloc.jularr.core.model.AnimeSummary
import de.juloc.jularr.core.model.ClientAccount
import de.juloc.jularr.core.model.ClientCapabilities
import de.juloc.jularr.core.model.ClientFeatureFlags
import de.juloc.jularr.core.model.ClientLibrary
import de.juloc.jularr.core.model.ClientLogin
import de.juloc.jularr.core.model.CompatibilityFallback
import de.juloc.jularr.core.model.ContinueWatchingItem
import de.juloc.jularr.core.model.CueResponse
import de.juloc.jularr.core.model.CueToken
import de.juloc.jularr.core.model.DevicePairingPollResult
import de.juloc.jularr.core.model.DevicePairingSession
import de.juloc.jularr.core.model.EpisodeDetail
import de.juloc.jularr.core.model.EpisodeProgress
import de.juloc.jularr.core.model.EpisodeProgressUpdate
import de.juloc.jularr.core.model.EpisodeSummary
import de.juloc.jularr.core.model.LearningCoverage
import de.juloc.jularr.core.model.LearningSubtitle
import de.juloc.jularr.core.model.MediaAvailability
import de.juloc.jularr.core.model.MediaTrack
import de.juloc.jularr.core.model.PlaybackHistoryItem
import de.juloc.jularr.core.model.OfflineDownloadPackage
import de.juloc.jularr.core.model.OfflineLibraryBookmarkEvent
import de.juloc.jularr.core.model.OfflineLibraryChapterPackage
import de.juloc.jularr.core.model.OfflineLibraryManifestPackage
import de.juloc.jularr.core.model.OfflineLibraryProgressEvent
import de.juloc.jularr.core.model.OfflineLibrarySyncResult
import de.juloc.jularr.core.model.OfflineProgressItem
import de.juloc.jularr.core.model.OfflineProgressResult
import de.juloc.jularr.core.model.PlaybackOption
import de.juloc.jularr.core.model.PlayerBootstrap
import de.juloc.jularr.core.model.PlayerEpisode
import de.juloc.jularr.core.model.PlayerMedia
import de.juloc.jularr.core.model.RootAvailability
import de.juloc.jularr.core.model.Season
import de.juloc.jularr.core.model.SpeechModel
import de.juloc.jularr.core.model.SpeechModelFileDescriptor
import de.juloc.jularr.core.model.SpeechModelsResponse
import de.juloc.jularr.core.model.SubtitleCue
import de.juloc.jularr.core.model.TermDetail
import de.juloc.jularr.core.model.TermStateResult
import de.juloc.jularr.core.model.TtsPreferences
import de.juloc.jularr.core.model.TtsPreferencesUpdate
import de.juloc.jularr.core.model.WatchlistItem
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL

class ClientApiHttpException(
    val statusCode: Int,
    val code: String?,
    override val message: String,
) : IOException(message)

class HttpJularrClientApi(
    origin: String,
    private val requestHeaders: () -> Map<String, String> = { emptyMap() },
    private val responseCookieSink: (List<String>) -> Unit = {},
) : JularrClientApi, JularrOfflineApi, JularrLibraryApi {
    private val originUri = normalizeOrigin(origin)

    override suspend fun getCapabilities(): ClientCapabilities =
        requestJson("GET", ClientApiRoutes.Capabilities).toCapabilities()

    override suspend fun login(credentials: ClientLogin): ClientAccount =
        requestJson(
            method = "POST",
            route = ClientApiRoutes.Login,
            body = JSONObject()
                .put("userName", credentials.userName)
                .put("password", credentials.password)
                .put("rememberMe", credentials.rememberMe),
        ).toAccount()

    override suspend fun logout() {
        requestJson("POST", ClientApiRoutes.Logout)
    }

    override suspend fun startDevicePairing(): DevicePairingSession =
        requestJson("POST", ClientApiRoutes.PairingStart).toPairingSession()

    override suspend fun pollDevicePairing(deviceCode: String): DevicePairingPollResult {
        val json = try {
            requestJson(
                method = "POST",
                route = ClientApiRoutes.PairingPoll,
                body = JSONObject().put("deviceCode", deviceCode),
            )
        } catch (exception: ClientApiHttpException) {
            if (exception.statusCode == 404) {
                return DevicePairingPollResult.Expired
            }
            throw exception
        }

        return if (json.optString("status") == "approved") {
            DevicePairingPollResult.Approved(json.getJSONObject("account").toAccount())
        } else {
            DevicePairingPollResult.Pending(json.optInt("intervalSeconds", 5))
        }
    }

    override suspend fun getMe(): ClientAccount =
        requestJson("GET", ClientApiRoutes.Me).toAccount()

    override suspend fun getLibrary(): ClientLibrary =
        requestJson("GET", ClientApiRoutes.Library).toLibrary()

    override suspend fun getContinueWatching(): List<ContinueWatchingItem> =
        requestJson("GET", ClientApiRoutes.ContinueWatching)
            .getJSONArray("items")
            .mapObjects { it.toContinueWatchingItem() }

    override suspend fun getPlaybackHistory(): List<PlaybackHistoryItem> =
        requestJson("GET", ClientApiRoutes.PlaybackHistory)
            .getJSONArray("items")
            .mapObjects { it.toPlaybackHistoryItem() }

    override suspend fun getWatchlist(): List<WatchlistItem> =
        requestJson("GET", ClientApiRoutes.Watchlist)
            .getJSONArray("items")
            .mapObjects { it.toWatchlistItem() }

    override suspend fun getAnime(animeId: String): AnimeDetail =
        requestJson("GET", ClientApiRoutes.anime(animeId)).toAnimeDetail()

    override suspend fun getEpisode(episodeId: String): EpisodeDetail =
        requestJson("GET", ClientApiRoutes.episode(episodeId)).toEpisodeDetail()

    override suspend fun getProgress(episodeId: String): EpisodeProgress =
        requestJson("GET", ClientApiRoutes.progress(episodeId)).toEpisodeProgress()

    override suspend fun setProgress(
        episodeId: String,
        update: EpisodeProgressUpdate,
    ): EpisodeProgress =
        requestJson(
            method = "PUT",
            route = ClientApiRoutes.progress(episodeId),
            body = JSONObject()
                .put("positionMs", update.positionMs)
                .put("durationMs", update.durationMs ?: JSONObject.NULL)
                .put("completed", update.completed),
        ).toEpisodeProgress()

    override suspend fun getPlayer(episodeId: String): PlayerBootstrap =
        requestJson("GET", ClientApiRoutes.player(episodeId)).toPlayerBootstrap()

    override suspend fun getOfflineDownload(episodeId: String): OfflineDownloadPackage {
        val json = requestJson("GET", ClientApiRoutes.offlineDownload(episodeId)).toString()
        return OfflineDownloadPackage(
            descriptor = OfflineDownloadJson.parseDescriptor(json),
            json = json,
        )
    }

    override suspend fun reconcileOfflineProgress(
        items: List<OfflineProgressItem>,
    ): List<OfflineProgressResult> =
        OfflineDownloadJson.parseProgressResults(
            requestJson(
                method = "POST",
                route = ClientApiRoutes.OfflineProgress,
                body = OfflineDownloadJson.progressItemsBody(items),
            ),
        )

    override suspend fun getLibraryManifest(workId: String): OfflineLibraryManifestPackage {
        val json = requestJson("GET", ClientApiRoutes.offlineLibraryManifest(workId)).toString()
        return OfflineLibraryManifestPackage(
            manifest = OfflineLibraryJson.parseManifest(json),
            json = json,
        )
    }

    override suspend fun getLibraryChapter(chapterId: String): OfflineLibraryChapterPackage {
        val json = requestJson("GET", ClientApiRoutes.offlineLibraryChapter(chapterId)).toString()
        return OfflineLibraryChapterPackage(
            payload = OfflineLibraryJson.parseChapter(json),
            json = json,
        )
    }

    override suspend fun syncLibrary(
        progress: List<OfflineLibraryProgressEvent>,
        bookmarks: List<OfflineLibraryBookmarkEvent>,
    ): OfflineLibrarySyncResult =
        OfflineLibraryJson.parseSyncResult(
            requestJson(
                method = "POST",
                route = ClientApiRoutes.OfflineLibrarySync,
                body = OfflineLibraryJson.syncBody(progress, bookmarks),
            ),
        )

    override suspend fun getCues(
        episodeId: String,
        trackId: String?,
        fromMs: Int?,
        toMs: Int?,
    ): CueResponse =
        requestJson(
            "GET",
            ClientApiRoutes.cues(
                episodeId = episodeId,
                trackId = trackId,
                fromMs = fromMs,
                toMs = toMs,
            ),
        ).toCueResponse()

    override suspend fun getMediaAvailability(
        mediaFileId: String,
        fresh: Boolean,
    ): MediaAvailability =
        requestJson(
            "GET",
            ClientApiRoutes.mediaAvailability(mediaFileId, fresh),
        ).toMediaAvailability()

    override suspend fun getTerm(termId: String): TermDetail =
        requestJson("GET", ClientApiRoutes.term(termId)).toTermDetail()

    override suspend fun setTermState(
        termId: String,
        state: String,
    ): TermStateResult =
        requestJson(
            method = "PUT",
            route = "${ClientApiRoutes.term(termId)}/state",
            body = JSONObject().put("state", state),
        ).toTermStateResult()

    override suspend fun getRootAvailability(rootId: String): RootAvailability =
        requestJson("GET", ClientApiRoutes.rootAvailability(rootId)).toRootAvailability()

    override suspend fun testRoot(rootId: String): RootAvailability =
        requestJson("POST", ClientApiRoutes.testRoot(rootId)).toRootAvailability()

    override suspend fun wakeRoot(rootId: String): RootAvailability =
        requestJson("POST", ClientApiRoutes.wakeRoot(rootId)).toRootAvailability()

    override suspend fun getTtsPreferences(): TtsPreferences =
        requestJson("GET", ClientApiRoutes.TtsPreferences).toTtsPreferences()

    override suspend fun updateTtsPreferences(update: TtsPreferencesUpdate): TtsPreferences =
        requestJson(
            method = "PUT",
            route = ClientApiRoutes.TtsPreferences,
            body = JSONObject().apply {
                put("providerId", update.providerId ?: JSONObject.NULL)
                put("rate", (update.rate as Any?) ?: JSONObject.NULL)
                put("pitch", (update.pitch as Any?) ?: JSONObject.NULL)
                put("volume", (update.volume as Any?) ?: JSONObject.NULL)
                put("voiceLanguage", update.voiceLanguage ?: JSONObject.NULL)
                put("voiceId", update.voiceId ?: JSONObject.NULL)
            },
        ).toTtsPreferences()

    override suspend fun getSpeechModels(): SpeechModelsResponse =
        requestJson("GET", ClientApiRoutes.SpeechModels).toSpeechModelsResponse()

    private suspend fun requestJson(
        method: String,
        route: String,
        body: JSONObject? = null,
    ): JSONObject = withContext(Dispatchers.IO) {
        val url = resolveSameOrigin(route)
        val connection = (url.openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = 15_000
            readTimeout = 30_000
            instanceFollowRedirects = false
            useCaches = false
            setRequestProperty("Accept", "application/json")
            setRequestProperty("X-Jularr-Client", "android")
            requestHeaders().forEach { (name, value) ->
                if (name.isNotBlank() && value.isNotBlank()) {
                    setRequestProperty(name, value)
                }
            }

            if (body != null) {
                doOutput = true
                setRequestProperty("Content-Type", "application/json; charset=utf-8")
            }
        }

        try {
            if (body != null) {
                connection.outputStream.bufferedWriter(Charsets.UTF_8).use {
                    it.write(body.toString())
                }
            }

            val status = connection.responseCode
            val setCookies = connection.headerFields
                .entries
                .firstOrNull { entry ->
                    entry.key?.equals("Set-Cookie", ignoreCase = true) == true
                }
                ?.value
                .orEmpty()
            if (setCookies.isNotEmpty()) {
                responseCookieSink(setCookies)
            }

            val stream = if (status in 200..299) {
                connection.inputStream
            } else {
                connection.errorStream
            }

            val payload = stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()

            if (status !in 200..299) {
                val error = payload.toJsonOrNull()
                throw ClientApiHttpException(
                    statusCode = status,
                    code = error?.stringOrNull("code"),
                    message = error?.stringOrNull("message")
                        ?: "Jularr API request failed with HTTP $status.",
                )
            }

            if (payload.isBlank()) {
                JSONObject()
            } else {
                JSONObject(payload)
            }
        } finally {
            connection.disconnect()
        }
    }

    private fun resolveSameOrigin(route: String): URL {
        val resolved = originUri.resolve(route)
        if (!sameOrigin(originUri, resolved)) {
            throw IllegalArgumentException("Jularr API route escaped the configured server origin.")
        }

        return resolved.toURL()
    }

    private fun JSONObject.toCapabilities() = ClientCapabilities(
        apiVersion = getInt("apiVersion"),
        minimumSupportedApiVersion = getInt("minimumSupportedApiVersion"),
        serverVersion = getString("serverVersion"),
        features = getJSONObject("features").let { features ->
            ClientFeatureFlagParser.parse { name ->
                if (name == "nativeSessionAuth" ||
                    name == "offlineDownloads" ||
                    name == "offlineLibrary" ||
                    name == "ttsPreferences" ||
                    name == "continueWatching" ||
                    name == "playbackHistory" ||
                    name == "watchlist" ||
                    name == "devicePairing" ||
                    name == "offlinePackages"
                ) {
                    features.optBoolean(name, false)
                } else {
                    features.getBoolean(name)
                }
            }
        },
    )

    private fun JSONObject.toAccount() = ClientAccount(
        profileId = getString("profileId"),
        userName = stringOrNull("userName"),
        role = getString("role"),
    )

    private fun JSONObject.toPairingSession() = DevicePairingSession(
        deviceCode = getString("deviceCode"),
        userCode = getString("userCode"),
        expiresInSeconds = getInt("expiresInSeconds"),
        intervalSeconds = getInt("intervalSeconds"),
    )

    private fun JSONObject.toLibrary() = ClientLibrary(
        anime = getJSONArray("anime").mapObjects { it.toAnimeSummary() },
    )

    private fun JSONObject.toAnimeSummary() = AnimeSummary(
        id = getString("id"),
        title = getString("title"),
        localTitle = getString("localTitle"),
        nativeTitle = stringOrNull("nativeTitle"),
        coverImageUrl = stringOrNull("coverImageUrl"),
        bannerImageUrl = stringOrNull("bannerImageUrl"),
        episodeCount = getInt("episodeCount"),
        seasonCount = getInt("seasonCount"),
        seasonYear = intOrNull("seasonYear"),
        format = stringOrNull("format"),
    )

    private fun JSONObject.toContinueWatchingItem() = ContinueWatchingItem(
        kind = getString("kind"),
        episodeId = getString("episodeId"),
        animeId = getString("animeId"),
        animeTitle = getString("animeTitle"),
        seasonNumber = getInt("seasonNumber"),
        episodeNumber = getInt("episodeNumber"),
        episodeTitle = getString("episodeTitle"),
        resumePositionMs = getLong("resumePositionMs"),
        durationMs = longOrNull("durationMs"),
        percent = getInt("percent"),
        updatedAtUtc = getString("updatedAtUtc"),
        coverImageUrl = stringOrNull("coverImageUrl"),
    )

    private fun JSONObject.toPlaybackHistoryItem() = PlaybackHistoryItem(
        id = getString("id"),
        episodeId = getString("episodeId"),
        animeId = getString("animeId"),
        animeTitle = getString("animeTitle"),
        seasonNumber = getInt("seasonNumber"),
        episodeNumber = getInt("episodeNumber"),
        episodeTitle = getString("episodeTitle"),
        startedAtUtc = getString("startedAtUtc"),
        lastPlayedAtUtc = getString("lastPlayedAtUtc"),
        positionMs = getLong("positionMs"),
        durationMs = longOrNull("durationMs"),
        reachedEnd = getBoolean("reachedEnd"),
    )

    private fun JSONObject.toWatchlistItem() = WatchlistItem(
        id = getString("id"),
        mediaType = getString("mediaType"),
        title = getString("title"),
        artworkUrl = stringOrNull("artworkUrl"),
        availability = getString("availability"),
        detailsUrl = stringOrNull("detailsUrl"),
        addedAtUtc = stringOrNull("addedAtUtc"),
    )

    private fun JSONObject.toAnimeDetail() = AnimeDetail(
        id = getString("id"),
        title = getString("title"),
        localTitle = getString("localTitle"),
        nativeTitle = stringOrNull("nativeTitle"),
        description = stringOrNull("description"),
        coverImageUrl = stringOrNull("coverImageUrl"),
        bannerImageUrl = stringOrNull("bannerImageUrl"),
        seasonYear = intOrNull("seasonYear"),
        format = stringOrNull("format"),
        seasons = getJSONArray("seasons").mapObjects { season ->
            Season(
                number = season.getInt("number"),
                episodes = season.getJSONArray("episodes").mapObjects { it.toEpisodeSummary() },
            )
        },
    )

    private fun JSONObject.toEpisodeSummary() = EpisodeSummary(
        id = getString("id"),
        seasonNumber = getInt("seasonNumber"),
        number = getInt("number"),
        title = getString("title"),
        hasMedia = getBoolean("hasMedia"),
        hasJapaneseLearningSubtitle = getBoolean("hasJapaneseLearningSubtitle"),
    )

    private fun JSONObject.toEpisodeDetail() = EpisodeDetail(
        id = getString("id"),
        animeId = getString("animeId"),
        animeTitle = getString("animeTitle"),
        title = getString("title"),
        seasonNumber = getInt("seasonNumber"),
        number = getInt("number"),
        hasMedia = getBoolean("hasMedia"),
        activeLearningSubtitleTrackId = stringOrNull("activeLearningSubtitleTrackId"),
        learningCueCount = getInt("learningCueCount"),
        learning = getJSONObject("learning").let { learning ->
            LearningCoverage(
                totalTerms = learning.getInt("totalTerms"),
                knownTerms = learning.getInt("knownTerms"),
                learningTerms = learning.getInt("learningTerms"),
                newTerms = learning.getInt("newTerms"),
            )
        },
    )

    private fun JSONObject.toEpisodeProgress() = EpisodeProgress(
        positionMs = getLong("positionMs"),
        durationMs = longOrNull("durationMs"),
        percent = getInt("percent"),
        isCompleted = getBoolean("isCompleted"),
        updatedAtUtc = stringOrNull("updatedAtUtc"),
    )

    private fun JSONObject.toPlayerBootstrap() = PlayerBootstrap(
        apiVersion = getInt("apiVersion"),
        episode = getJSONObject("episode").let { episode ->
            PlayerEpisode(
                id = episode.getString("id"),
                animeId = episode.getString("animeId"),
                animeTitle = episode.getString("animeTitle"),
                title = episode.getString("title"),
                seasonNumber = episode.getInt("seasonNumber"),
                number = episode.getInt("number"),
            )
        },
        media = objectOrNull("media")?.toPlayerMedia(),
        audioTracks = getJSONArray("audioTracks").mapObjects { it.toMediaTrack() },
        subtitleTracks = getJSONArray("subtitleTracks").mapObjects { it.toMediaTrack() },
        learningSubtitles = getJSONArray("learningSubtitles").mapObjects { learning ->
            LearningSubtitle(
                trackId = learning.getString("trackId"),
                language = learning.getString("language"),
                format = learning.getString("format"),
                isActive = learning.getBoolean("isActive"),
                cuesUrl = learning.getString("cuesUrl"),
            )
        },
        activeLearningSubtitleTrackId = stringOrNull("activeLearningSubtitleTrackId"),
        defaultAudioTrackId = stringOrNull("defaultAudioTrackId"),
        defaultSubtitleTrackId = stringOrNull("defaultSubtitleTrackId"),
        fallback = getJSONObject("fallback").let { fallback ->
            CompatibilityFallback(
                available = fallback.getBoolean("available"),
                kind = fallback.stringOrNull("kind"),
                seekableWithinStream = fallback.getBoolean("seekableWithinStream"),
                canRestartAtPosition = fallback.getBoolean("canRestartAtPosition"),
                url = fallback.stringOrNull("url"),
            )
        },
    )

    private fun JSONObject.toPlayerMedia() = PlayerMedia(
        mediaFileId = getString("mediaFileId"),
        fileName = getString("fileName"),
        contentType = getString("contentType"),
        sizeBytes = longOrNull("sizeBytes"),
        durationMs = longOrNull("durationMs"),
        videoCodec = stringOrNull("videoCodec"),
        pixelFormat = stringOrNull("pixelFormat"),
        audioCodec = stringOrNull("audioCodec"),
        directContentUrl = getString("directContentUrl"),
        supportsRangeRequests = getBoolean("supportsRangeRequests"),
        device = getJSONObject("device").toPlaybackOption(),
        server = getJSONObject("server").toPlaybackOption(),
        availability = getJSONObject("availability").toMediaAvailability(),
    )

    private fun JSONObject.toPlaybackOption() = PlaybackOption(
        availability = getString("availability"),
        message = getString("message"),
        usesLiveStream = getBoolean("usesLiveStream"),
    )

    private fun JSONObject.toMediaTrack() = MediaTrack(
        id = getString("id"),
        streamIndex = getInt("streamIndex"),
        kind = getString("kind"),
        codec = stringOrNull("codec"),
        language = stringOrNull("language"),
        title = stringOrNull("title"),
        isDefault = getBoolean("isDefault"),
        isForced = getBoolean("isForced"),
        isText = getBoolean("isText"),
    )

    private fun JSONObject.toMediaAvailability() = MediaAvailability(
        state = getString("state"),
        retryable = getBoolean("retryable"),
        retryAfterMs = getInt("retryAfterMs"),
        canWake = getBoolean("canWake"),
        rootId = stringOrNull("rootId"),
        availabilityUrl = getString("availabilityUrl"),
        wakeUrl = stringOrNull("wakeUrl"),
    )

    private fun JSONObject.toRootAvailability() = RootAvailability(
        rootId = getString("rootId"),
        state = getString("state"),
        retryable = getBoolean("retryable"),
        checkedAtUtc = getString("checkedAtUtc"),
        lastAvailableAtUtc = stringOrNull("lastAvailableAtUtc"),
        wakeConfigured = getBoolean("wakeConfigured"),
        diagnosticCode = stringOrNull("diagnosticCode"),
    )

    private fun JSONObject.toTtsPreferences(): TtsPreferences {
        val voiceIdsJson = optJSONObject("voiceIds")
        val voiceIds = buildMap {
            voiceIdsJson?.keys()?.forEach { key -> put(key, voiceIdsJson.getString(key)) }
        }

        return TtsPreferences(
            providerId = getString("providerId"),
            voiceIds = voiceIds,
            rate = getDouble("rate"),
            pitch = getDouble("pitch"),
            volume = getDouble("volume"),
        )
    }

    private fun JSONObject.toSpeechModelsResponse() = SpeechModelsResponse(
        models = getJSONArray("models").mapObjects { it.toSpeechModel() },
    )

    private fun JSONObject.toSpeechModel() = SpeechModel(
        providerId = getString("providerId"),
        modelId = getString("modelId"),
        version = getString("version"),
        languages = getJSONArray("languages").let { array ->
            (0 until array.length()).map { index -> array.getString(index) }
        },
        voices = getJSONArray("voices").let { array ->
            (0 until array.length()).map { index -> array.getString(index) }
        },
        files = getJSONArray("files").mapObjects { file ->
            SpeechModelFileDescriptor(
                name = file.getString("name"),
                url = file.getString("url"),
                sizeBytes = file.getLong("sizeBytes"),
                sha256 = file.getString("sha256"),
            )
        },
        totalSizeBytes = getLong("totalSizeBytes"),
        minimumCompatibleVersion = getString("minimumCompatibleVersion"),
    )

    private fun JSONObject.toCueResponse() = CueResponse(
        trackId = stringOrNull("trackId"),
        fromMs = intOrNull("fromMs"),
        toMs = intOrNull("toMs"),
        cues = getJSONArray("cues").mapObjects { cue ->
            SubtitleCue(
                id = cue.getLong("id"),
                startMs = cue.getInt("startMs"),
                endMs = cue.getInt("endMs"),
                text = cue.getString("text"),
                tokens = cue.getJSONArray("tokens").mapObjects { token ->
                    CueToken(
                        surface = token.getString("surface"),
                        termId = token.stringOrNull("termId"),
                        canonical = token.stringOrNull("canonical"),
                        reading = token.stringOrNull("reading"),
                        meaning = token.stringOrNull("meaning"),
                        state = token.getString("state"),
                    )
                },
            )
        },
    )

    private fun JSONObject.toTermDetail() = TermDetail(
        id = getString("id"),
        canonical = getString("canonical"),
        reading = stringOrNull("reading"),
        meaning = stringOrNull("meaning"),
        state = getString("state"),
    )

    private fun JSONObject.toTermStateResult() = TermStateResult(
        termId = getString("termId"),
        state = getString("state"),
    )

    private fun JSONObject.objectOrNull(name: String): JSONObject? =
        if (!has(name) || isNull(name)) null else getJSONObject(name)

    private fun JSONObject.stringOrNull(name: String): String? =
        if (!has(name) || isNull(name)) null else getString(name)

    private fun JSONObject.intOrNull(name: String): Int? =
        if (!has(name) || isNull(name)) null else getInt(name)

    private fun JSONObject.longOrNull(name: String): Long? =
        if (!has(name) || isNull(name)) null else getLong(name)

    private inline fun <T> JSONArray.mapObjects(transform: (JSONObject) -> T): List<T> =
        buildList(length()) {
            for (index in 0 until length()) {
                add(transform(getJSONObject(index)))
            }
        }

    private fun String.toJsonOrNull(): JSONObject? =
        runCatching { JSONObject(this) }.getOrNull()

    companion object {
        private fun normalizeOrigin(origin: String): URI {
            val uri = URI(origin.trim())
            require(uri.scheme.equals("http", true) || uri.scheme.equals("https", true)) {
                "Jularr server origin must use http or https."
            }
            require(!uri.host.isNullOrBlank()) {
                "Jularr server origin must include a host."
            }
            require(uri.userInfo == null && uri.query == null && uri.fragment == null) {
                "Jularr server origin cannot contain credentials, query or fragment."
            }
            require(uri.path.isNullOrBlank() || uri.path == "/") {
                "Jularr server origin must not contain an application path."
            }

            return URI(
                uri.scheme.lowercase(),
                null,
                uri.host.lowercase(),
                uri.port,
                "/",
                null,
                null,
            )
        }

        private fun sameOrigin(left: URI, right: URI): Boolean =
            left.scheme.equals(right.scheme, true) &&
                left.host.equals(right.host, true) &&
                effectivePort(left) == effectivePort(right)

        private fun effectivePort(uri: URI): Int =
            if (uri.port >= 0) {
                uri.port
            } else if (uri.scheme.equals("https", true)) {
                443
            } else {
                80
            }
    }
}


internal object ClientFeatureFlagParser {
    fun parse(readBoolean: (String) -> Boolean) = ClientFeatureFlags(
        library = readBoolean("library"),
        nativeSessionAuth = readBoolean("nativeSessionAuth"),
        nativePlayerBootstrap = readBoolean("nativePlayerBootstrap"),
        directPlayback = readBoolean("directPlayback"),
        playbackProgress = readBoolean("playbackProgress"),
        httpRangeRequests = readBoolean("httpRangeRequests"),
        mediaTrackMetadata = readBoolean("mediaTrackMetadata"),
        normalizedLearningCues = readBoolean("normalizedLearningCues"),
        learningStateMutation = readBoolean("learningStateMutation"),
        liveMp4Fallback = readBoolean("liveMp4Fallback"),
        hlsFallback = readBoolean("hlsFallback"),
        playbackSessions = readBoolean("playbackSessions"),
        companionPairing = readBoolean("companionPairing"),
        companionControl = readBoolean("companionControl"),
        storageAvailability = readBoolean("storageAvailability"),
        ownerWakeOnLan = readBoolean("ownerWakeOnLan"),
        offlineDownloads = readBoolean("offlineDownloads"),
        offlineLibrary = readBoolean("offlineLibrary"),
        ttsPreferences = readBoolean("ttsPreferences"),
        continueWatching = readBoolean("continueWatching"),
        playbackHistory = readBoolean("playbackHistory"),
        watchlist = readBoolean("watchlist"),
        devicePairing = readBoolean("devicePairing"),
        offlinePackages = readBoolean("offlinePackages"),
    )
}
