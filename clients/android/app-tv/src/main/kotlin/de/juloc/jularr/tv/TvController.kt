package de.juloc.jularr.tv

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import de.juloc.jularr.core.api.AppApiClient
import de.juloc.jularr.core.api.DeviceTokenStore
import de.juloc.jularr.core.api.HttpAppApiClient
import de.juloc.jularr.core.design.DesignTokens
import de.juloc.jularr.core.model.ContinueItemV1
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePlatform
import de.juloc.jularr.core.model.DiscoverItemV1
import de.juloc.jularr.core.model.DiscoverScopeV1
import de.juloc.jularr.core.model.DiscoverShelfV1
import de.juloc.jularr.core.model.DiscoverSuggestionV1
import de.juloc.jularr.core.model.HomeShelfV1
import de.juloc.jularr.core.model.PlaybackPlanV1
import de.juloc.jularr.core.model.ProfileSelectionV1
import de.juloc.jularr.core.model.WorkCardV1
import de.juloc.jularr.core.model.WorkDetailsV1
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * The state of the television app: the instance it talks to, the pairing, the texts of the
 * server and the shelves of the home page. The screens read this state; nothing is decided
 * twice.
 */
class TvController(context: Context) {
    private val settings = TvSettings(context)
    private val deviceTokens = DeviceTokenStore(TvTokenStorage(context))
    private val scope = CoroutineScope(Dispatchers.Main.immediate + SupervisorJob())

    /** The focus of every screen, so a screen that is entered again starts where it stopped. */
    val focusMemory = TvFocusMemory()

    var phase by mutableStateOf(TvPhase.Setup)
        private set

    var address by mutableStateOf(settings.address)
        private set

    var pairingCode by mutableStateOf<String?>(null)
        private set

    var designTokens by mutableStateOf<DesignTokens?>(null)
        private set

    var texts by mutableStateOf(TvTexts.empty)
        private set

    var route by mutableStateOf(TvRoute.Home)
        private set

    /** The skin of the instance the television shows. */
    var skin by mutableStateOf(settings.skin)
        private set

    /** The brightness of the television: the dark mode of the desktop. */
    var dark by mutableStateOf(settings.dark)
        private set

    var shelves by mutableStateOf(emptyList<TvShelf>())
        private set

    var watchlist by mutableStateOf(emptyList<TvCard>())
        private set

    var library by mutableStateOf(emptyList<TvCard>())
        private set

    var scopes by mutableStateOf(emptyList<DiscoverScopeV1>())
        private set

    var profiles by mutableStateOf(emptyList<ProfileSelectionV1>())
        private set

    /** The name of the profile the television works as. */
    var profileName by mutableStateOf("")
        private set

    /** What the viewer types in the search. */
    var searchQuery by mutableStateOf("")
        private set

    /** The titles the instance suggests for the text. */
    var searchSuggestions by mutableStateOf(emptyList<DiscoverSuggestionV1>())
        private set

    /** The titles that match the text. */
    var searchResults by mutableStateOf(emptyList<DiscoverItemV1>())
        private set

    /** The ranked lists of the instance; the trending ones stand on the right of the search. */
    var trending by mutableStateOf(emptyList<DiscoverItemV1>())
        private set

    /** The details of the title the viewer opened. */
    var details by mutableStateOf<WorkDetailsV1?>(null)
        private set

    /** Where the focus is now; the D-pad moves between the areas in the reading order. */
    var focusArea by mutableStateOf(TvFocusArea.Rail)
        private set

    /** The order of the focus inside the content: the rows from top to bottom. */
    val focusOrder = TvFocusOrder()

    /** The entry of the content the focus is in. */
    var focusedEntry by mutableStateOf<FocusRequester?>(null)
        private set

    /** The plan of the running playback. */
    var playbackPlan by mutableStateOf<PlaybackPlanV1?>(null)
        private set

    /** The title the television plays. */
    var playingTitle by mutableStateOf("")
        private set

    var error by mutableStateOf<String?>(null)
        private set

    var busy by mutableStateOf(false)
        private set

    private var api: AppApiClient? = null
    private var previousRoute = TvRoute.Home
    private var searchJob: Job? = null

    /**
     * Starts the app: a television with a known address connects to it right away, so it shows
     * the library after a restart without a new pairing.
     */
    fun start() {
        if (settings.address.isBlank()) {
            phase = TvPhase.Setup
            return
        }
        connect(settings.address)
    }

    /** Connects to the instance of the address and pairs the device. */
    fun connect(target: String) {
        if (busy) return
        val address = target.trim().trimEnd('/')
        if (address.isEmpty()) {
            error = "The address of the instance is missing."
            return
        }
        scope.launch {
            busy = true
            error = null
            this@TvController.address = address
            // The address belongs to this television; it connects to it on the next start.
            settings.address = address
            try {
                val client = HttpAppApiClient(address, deviceTokens)
                api = client
                designTokens = client.designTokens()
                val presentation = client.instancePresentation()
                texts = TvTexts(client.uiTexts(presentation.uiLocaleId))
                if (deviceTokens.current != null && tryUseSession(client)) {
                    phase = TvPhase.Ready
                    loadHome()
                } else {
                    startPairing(client)
                }
            } catch (failure: Exception) {
                error = failure.message ?: failure::class.java.simpleName
                android.util.Log.e("JularrTv", "The instance could not be read.", failure)
            }
            busy = false
        }
    }

    /** Starts a pairing; the television shows the code and waits for the account. */
    private suspend fun startPairing(client: AppApiClient) {
        val pairing = client.startPairing(
            DevicePairingRequest(deviceName = deviceName(), devicePlatform = DevicePlatform.AndroidTv),
        )
        pairingCode = pairing.code
        phase = TvPhase.Pairing
        while (true) {
            delay(ClaimIntervalMs)
            val claim = client.claimPairing(pairing.pairingId)
            if (claim.isPaired) {
                break
            }
        }
        pairingCode = null
        phase = TvPhase.Ready
        loadHome()
    }

    /** Reads the shelves of the home page and the lists of the rail. */
    suspend fun loadHome() {
        val client = api ?: return
        val layout = client.homeLayout()
        val continuing = client.continueWatching()
        val recent = client.library()
        scopes = client.discoverScopes()
        shelves = buildShelves(layout, continuing, recent)
        watchlist = client.watchlist().map { it.toTvCard(texts) }
        library = recent.map { it.toTvCard(texts) }
        val profiles = client.profiles()
        this.profiles = profiles
        val current = client.authentication().profileId
        profileName = profiles.firstOrNull { it.id == current }?.displayName.orEmpty()
    }

    /** Switches the television to another profile of its account. */
    suspend fun switchProfile(profileId: Long) {
        val client = api ?: return
        client.selectProfile(profileId)
        loadHome()
    }

    /** Opens another surface of the rail. */
    fun open(target: TvRoute) {
        route = target
    }

    /** The focus moved into an area of the screen. */
    fun focusMovedTo(area: TvFocusArea) {
        focusArea = area
    }

    /** The focus moved onto an entry of the content. */
    fun focusMovedTo(entry: FocusRequester?) {
        focusedEntry = entry
    }

    /**
     * The D-pad moves the focus inside the content: left and right inside the row, up and down
     * to the row above or below.
     */
    fun moveFocus(direction: TvFocusDirection): Boolean {
        val entry = focusedEntry ?: return false
        val next = when (direction) {
            TvFocusDirection.Left -> focusOrder.moveInside(entry, forward = false)
            TvFocusDirection.Right -> focusOrder.moveInside(entry, forward = true)
            TvFocusDirection.Up -> focusOrder.moveToRow(entry, up = true)
            TvFocusDirection.Down -> focusOrder.moveToRow(entry, up = false)
        } ?: return false
        next.requestFocus()
        return true
    }

    /** The first entry of the content; the D-pad returns here from the top row. */
    fun focusFirstEntry(): Boolean {
        val first = focusOrder.firstEntry() ?: return false
        first.requestFocus()
        return true
    }

    /**
     * Opens the search of the instance. The viewer types with the keyboard of the television;
     * the suggestions of the instance pop up while the text grows and the trending titles of
     * the instance stand on the right.
     */
    fun openSearch() {
        previousRoute = route
        route = TvRoute.Search
        searchQuery = ""
        searchSuggestions = emptyList()
        searchResults = emptyList()
        loadTrending()
    }

    /** Reads the trending titles of the instance for the right side of the search. */
    private fun loadTrending() {
        if (trending.isNotEmpty()) {
            return
        }
        scope.launch {
            val client = api ?: return@launch
            runCatching { client.discoverShelves() }.onSuccess { shelves ->
                trending = shelves
                    .filter { it.discoverFeedTypeId == DiscoverShelfV1.FeedTrending }
                    .flatMap { it.items }
                    // A title can stand in more than one scope; it appears once.
                    .distinctBy { it.workId ?: it.tmdbId }
                    .take(TrendingTitles)
            }
        }
    }

    /** A key of the keyboard of the television. */
    fun type(key: String) {
        val text = when (key) {
            "␣" -> searchQuery + " "
            "⌫" -> searchQuery.dropLast(1)
            else -> searchQuery + key
        }
        searchQuery = text
        runSearch(text)
    }

    /** What the viewer said enters the field of the search. */
    fun heard(text: String) {
        searchQuery = text
        runSearch(text)
    }

    /** Clears the text of the search. */
    fun clearSearch() {
        searchQuery = ""
        searchSuggestions = emptyList()
        searchResults = emptyList()
    }

    /** Opens the title the viewer chose. */
    fun openDetails(workId: Long?) {
        if (workId == null) {
            return
        }
        previousRoute = route
        route = TvRoute.Details
        details = null
        scope.launch {
            val client = api ?: return@launch
            runCatching { client.work(workId) }
                .onSuccess { details = it }
                .onFailure { error = it.message ?: it::class.java.simpleName }
        }
    }

    /** The suggestion opens the title it names. */
    fun openSuggestion(suggestion: DiscoverSuggestionV1) {
        openDetails(suggestion.workId)
    }

    /**
     * OK on a card: a title the profile continues is played at once, every other title opens
     * its details.
     */
    fun openCard(card: TvCard) {
        val asset = card.mediaAssetId
        if (asset != null) {
            play(asset, card.title)
            return
        }
        openDetails(card.workId)
    }

    /** Starts the playback of an asset. */
    fun play(mediaAssetId: Long, title: String) {
        val client = api ?: return
        previousRoute = route
        route = TvRoute.Player
        playingTitle = title
        playbackPlan = null
        scope.launch {
            runCatching { client.startPlayback(mediaAssetId) }
                .onSuccess { plan ->
                    playbackPlan = plan
                    onPlanReady?.invoke(plan)
                }
                .onFailure { error = it.message ?: it::class.java.simpleName }
        }
    }

    /** The player tells the controller that the stream is open. */
    var onPlanReady: ((PlaybackPlanV1) -> Unit)? = null

    /** The token of the device; the stream of the instance needs it. */
    val deviceToken: String?
        get() = deviceTokens.current

    /** The address of the instance; the stream of the instance is read from there. */
    val origin: String
        get() = address

    /** Writes the progress of the running playback back to the instance. */
    fun reportProgress(positionMs: Long, durationMs: Long?, isEnded: Boolean, isCompleted: Boolean) {
        val plan = playbackPlan ?: return
        val client = api ?: return
        scope.launch {
            runCatching {
                client.updatePlayback(plan.playbackSessionId, positionMs, durationMs, isEnded, isCompleted)
            }
        }
    }

    /** Back returns to the surface the television came from; it tells when it did. */
    fun back(): Boolean {
        if (route != TvRoute.Search && route != TvRoute.Details) {
            return false
        }
        route = previousRoute
        details = null
        return true
    }

    /** Searches the titles of the instance; the text is read after a short pause. */
    private fun runSearch(text: String) {
        searchJob?.cancel()
        if (text.isBlank()) {
            searchSuggestions = emptyList()
            searchResults = emptyList()
            return
        }
        searchJob = scope.launch {
            delay(SearchDelayMs)
            val client = api ?: return@launch
            runCatching { client.suggestions(text) }.onSuccess { searchSuggestions = it }
            runCatching { client.search(text) }.onSuccess { searchResults = it }
        }
    }

    /** Switches the skin of the television. */
    fun useSkin(name: String) {
        skin = name
        settings.skin = name
    }

    /** Switches the brightness of the television. */
    fun useDark(value: Boolean) {
        dark = value
        settings.dark = value
    }

    /** Ends the pairing of this device; the television pairs again on the next start. */
    fun forget() {
        deviceTokens.clear()
        phase = TvPhase.Setup
        pairingCode = null
        shelves = emptyList()
        watchlist = emptyList()
        library = emptyList()
        scopes = emptyList()
        error = null
    }

    /** The name of this device in the session list of the account. */
    private fun deviceName(): String = "Android TV"

    /** Checks whether the stored token still works. */
    private suspend fun tryUseSession(client: AppApiClient): Boolean = runCatching {
        client.capabilities()
        true
    }.getOrElse {
        deviceTokens.clear()
        false
    }

    /** Builds the shelves of the home page in the order of the profile. */
    private fun buildShelves(
        layout: List<HomeShelfV1>,
        continuing: List<ContinueItemV1>,
        recent: List<WorkCardV1>,
    ): List<TvShelf> = layout.filter { !it.isHidden }.mapNotNull { shelf ->
        when (shelf.homeShelfTypeId) {
            HomeShelfV1.ContinueWatching -> shelfOf(
                "continue-watching",
                texts["discover.continueWatching"],
                continuing.filter { it.mediaAssetTypeId == ContinueItemV1.AssetVideo }
                    .map { it.toTvCard(texts) },
            )

            HomeShelfV1.ContinueListening -> shelfOf(
                "continue-listening",
                texts["discover.continueListening"],
                continuing.filter { it.mediaAssetTypeId == ContinueItemV1.AssetAudio }
                    .map { it.toTvCard(texts) },
            )

            HomeShelfV1.ContinueReading -> shelfOf(
                "continue-reading",
                texts["media.continueReading"],
                continuing.filter {
                    it.mediaAssetTypeId == ContinueItemV1.AssetEbook ||
                        it.mediaAssetTypeId == ContinueItemV1.AssetComic
                }.map { it.toTvCard(texts) },
            )

            HomeShelfV1.RecentlyAdded -> shelfOf(
                "recently-added",
                texts["discover.recentlyAdded"],
                recent.map { it.toTvCard(texts) },
            )

            // The recommendations of the providers come with the next package.
            else -> null
        }
    }.filter { it.cards.isNotEmpty() }

    private fun shelfOf(key: String, title: String, cards: List<TvCard>): TvShelf =
        TvShelf(key, title, cards)

    private companion object {
        const val ClaimIntervalMs = 5_000L
        const val SearchDelayMs = 150L
        const val TrendingTitles = 12
    }
}
