package de.juloc.jularr.core.api

import de.juloc.jularr.core.design.DesignTokens
import de.juloc.jularr.core.model.AuthenticationV1
import de.juloc.jularr.core.model.CollectionV1
import de.juloc.jularr.core.model.ContinueItemV1
import de.juloc.jularr.core.model.DevicePairingClaim
import de.juloc.jularr.core.model.DevicePairingRequest
import de.juloc.jularr.core.model.DevicePairingStarted
import de.juloc.jularr.core.model.DiscoverItemV1
import de.juloc.jularr.core.model.DiscoverScopeV1
import de.juloc.jularr.core.model.DiscoverShelfV1
import de.juloc.jularr.core.model.DiscoverSuggestionV1
import de.juloc.jularr.core.model.HomeShelfV1
import de.juloc.jularr.core.model.InstanceCapabilities
import de.juloc.jularr.core.model.InstancePresentationV1
import de.juloc.jularr.core.model.PlaybackPlanV1
import de.juloc.jularr.core.model.ProfileSelectionV1
import de.juloc.jularr.core.model.UiLocaleV1
import de.juloc.jularr.core.model.WorkCardV1
import de.juloc.jularr.core.model.WorkDetailsV1

/**
 * The app services a client needs: pair the device, read what the instance allows and keep the
 * session alive. Every answer comes from the versioned app services of the Backend.
 */
interface AppApiClient {
    /** Starts a pairing; the device shows the returned code. */
    suspend fun startPairing(request: DevicePairingRequest): DevicePairingStarted

    /** Fetches the session of a pairing; it succeeds once a signed-in profile confirmed the code. */
    suspend fun claimPairing(pairingId: String): DevicePairingClaim

    /** Reads the permissions and the modules of the paired device. */
    suspend fun capabilities(): InstanceCapabilities

    /** Reads the signed-in state: the account, the profile and the locale of the instance. */
    suspend fun authentication(): AuthenticationV1

    /** Reads the public display values of the instance. */
    suspend fun instancePresentation(): InstancePresentationV1

    /** Reads the server's texts of a locale; the client shows them instead of its own words. */
    suspend fun uiTexts(uiLocaleId: Long): Map<String, String>

    /** Reads one page of the profile's watchlist. */
    suspend fun watchlist(page: Int = 1, pageSize: Int = 25): List<WorkCardV1>

    /** Reads one page of the profile's library, the recent works. */
    suspend fun library(page: Int = 1, pageSize: Int = 25): List<WorkCardV1>

    /** Reads the shared design tokens the instance serves. */
    suspend fun designTokens(): DesignTokens

    /** Reads the titles the profile continues: watched, listened to and read. */
    suspend fun continueWatching(): List<ContinueItemV1>

    /** Reads the shelves of the profile's home page in their order, the hidden ones among them. */
    suspend fun homeLayout(): List<HomeShelfV1>

    /** Reads the media types the profile may browse. */
    suspend fun discoverScopes(): List<DiscoverScopeV1>

    /** Searches the titles of the instance. */
    suspend fun search(query: String, mediaTypeId: Int? = null, page: Int = 1): List<DiscoverItemV1>

    /** Reads the titles the instance suggests for what the viewer types. */
    suspend fun suggestions(query: String, scopeSlug: String? = null): List<DiscoverSuggestionV1>

    /** Reads the details of a title of the instance. */
    suspend fun work(workId: Long): WorkDetailsV1

    /** Reads the ranked lists of the Discover of the instance; without a scope every scope. */
    suspend fun discoverShelves(scopeSlug: String? = null): List<DiscoverShelfV1>

    /** Starts the playback of an asset; the answer carries the stream and the resume point. */
    suspend fun startPlayback(mediaAssetId: Long): PlaybackPlanV1

    /** Writes the progress of a running playback back to the instance. */
    suspend fun updatePlayback(
        playbackSessionId: String,
        positionMs: Long,
        durationMs: Long?,
        isEnded: Boolean,
        isCompleted: Boolean,
    )

    /** Reads the languages the instance offers for the interface. */
    suspend fun locales(): List<UiLocaleV1>

    /** Changes the language of the interface of the profile. */
    suspend fun updateProfileLanguage(profileId: Long, uiLocaleId: Long)

    /** Reads the collections of the profile. */
    suspend fun collections(): List<CollectionV1>

    /** Reads the titles of a collection. */
    suspend fun collectionWorks(collectionId: Long): List<WorkCardV1>

    /** Puts a title into a collection or takes it out. */
    suspend fun setCollectionWork(collectionId: Long, workId: Long, member: Boolean)

    /** Creates a collection of the profile and answers its id. */
    suspend fun createCollection(name: String): Long

    /** Changes the name of a collection. */
    suspend fun renameCollection(collectionId: Long, name: String, version: Long)

    /** Removes a collection of the profile. */
    suspend fun deleteCollection(collectionId: Long)

    /** Reads the profiles the account of the device may use. */
    suspend fun profiles(): List<ProfileSelectionV1>

    /** Switches the device to another profile; the answer carries a new device token. */
    suspend fun selectProfile(profileId: Long)
}
