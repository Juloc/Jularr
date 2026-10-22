package de.juloc.jularr.core.update

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

/** Raised when the repository has no published release yet (`/releases/latest` 404s). */
class GitHubReleaseNotFoundException(message: String) : IOException(message)

interface GitHubReleasesApi {
    /**
     * The latest published release for [owner]/[repo] that is neither a draft nor a
     * pre-release. GitHub's `/releases/latest` endpoint already excludes both, so the
     * client never has to reinterpret that policy locally.
     */
    suspend fun getLatestRelease(owner: String, repo: String): GitHubRelease
}

/**
 * Unauthenticated GET against the public GitHub REST API — no token, no secret, and no
 * arbitrary caller-supplied URL: the owner/repo are fixed by [GitHubUpdateChecker] to
 * the configured Jularr repository.
 */
class HttpGitHubReleasesApi(
    private val userAgent: String = "Jularr-Android-Updater",
) : GitHubReleasesApi {

    override suspend fun getLatestRelease(owner: String, repo: String): GitHubRelease =
        withContext(Dispatchers.IO) {
            val url = URL("https://api.github.com/repos/$owner/$repo/releases/latest")
            val connection = (url.openConnection() as HttpURLConnection).apply {
                requestMethod = "GET"
                connectTimeout = 15_000
                readTimeout = 20_000
                instanceFollowRedirects = true
                useCaches = false
                setRequestProperty("Accept", "application/vnd.github+json")
                setRequestProperty("User-Agent", userAgent)
                setRequestProperty("X-GitHub-Api-Version", "2022-11-28")
            }

            try {
                val status = connection.responseCode
                if (status == HttpURLConnection.HTTP_NOT_FOUND) {
                    throw GitHubReleaseNotFoundException("No published release was found for $owner/$repo.")
                }

                val stream = if (status in 200..299) connection.inputStream else connection.errorStream
                val payload = stream?.bufferedReader(Charsets.UTF_8)?.use { it.readText() }.orEmpty()

                if (status !in 200..299) {
                    throw IOException("GitHub Releases request failed with HTTP $status.")
                }

                parseRelease(JSONObject(payload))
            } finally {
                connection.disconnect()
            }
        }

    private fun parseRelease(json: JSONObject): GitHubRelease = GitHubRelease(
        tagName = json.getString("tag_name"),
        name = json.stringOrNull("name"),
        htmlUrl = json.stringOrNull("html_url") ?: "",
        draft = json.optBoolean("draft", false),
        prerelease = json.optBoolean("prerelease", false),
        body = json.stringOrNull("body"),
        assets = json.optJSONArray("assets")?.let { array ->
            (0 until array.length()).map { index ->
                val asset = array.getJSONObject(index)
                GitHubReleaseAsset(
                    name = asset.getString("name"),
                    downloadUrl = asset.getString("browser_download_url"),
                    sizeBytes = asset.optLong("size", 0L),
                )
            }
        }.orEmpty(),
    )

    private fun JSONObject.stringOrNull(name: String): String? =
        if (!has(name) || isNull(name)) null else getString(name)
}
