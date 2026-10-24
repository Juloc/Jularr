package de.juloc.jularr.mobile

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.net.http.SslError
import android.os.Bundle
import android.webkit.SslErrorHandler
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import de.juloc.jularr.mobile.offline.library.LibraryLocalResponse
import de.juloc.jularr.mobile.offline.library.LibraryWebPages
import java.io.ByteArrayInputStream

@Composable
fun JularrWebShell(
    origin: ServerOrigin,
    restoredState: Bundle?,
    modifier: Modifier = Modifier,
    onWebViewChanged: (WebView?) -> Unit,
    onEpisodeRequested: (String) -> Unit,
    onSecurityError: (String) -> Unit,
    onPageLoaded: () -> Unit = {},
    onLibraryWorkPageChanged: (String?) -> Unit = {},
    resolveLibraryRequest: ((String) -> LibraryLocalResponse?)? = null,
) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val webSession = remember(origin.value) { WebSession(origin) }
    val webView = remember(origin.value) {
        WebView(context).apply {
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.allowFileAccess = false
            settings.allowContentAccess = false
            settings.javaScriptCanOpenWindowsAutomatically = false
            settings.setSupportMultipleWindows(false)
            settings.mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
            settings.mediaPlaybackRequiresUserGesture = true
            settings.userAgentString =
                settings.userAgentString + " JularrAndroid/0.1"

            removeJavascriptInterface("searchBoxJavaBridge_")
            removeJavascriptInterface("accessibility")
            removeJavascriptInterface("accessibilityTraversal")

            webSession.configureFor(this)

            webViewClient = object : WebViewClient() {
                override fun shouldInterceptRequest(
                    view: WebView,
                    request: WebResourceRequest,
                ): WebResourceResponse? {
                    if (resolveLibraryRequest == null || request.method?.uppercase() != "GET") {
                        return null
                    }
                    val candidate = runCatching { java.net.URI(request.url.toString()) }.getOrNull()
                        ?: return null
                    if (!origin.isSameOrigin(candidate)) {
                        return null
                    }
                    val local = resolveLibraryRequest(candidate.path ?: "") ?: return null
                    return WebResourceResponse(
                        local.contentType,
                        "utf-8",
                        ByteArrayInputStream(local.bytes),
                    )
                }

                override fun shouldOverrideUrlLoading(
                    view: WebView,
                    request: WebResourceRequest,
                ): Boolean {
                    if (!request.isForMainFrame) {
                        return false
                    }

                    return handleNavigation(
                        origin = origin,
                        rawUrl = request.url.toString(),
                        onEpisodeRequested = onEpisodeRequested,
                        onExternal = { uri ->
                            openExternal(
                                activity = context as? Activity,
                                uri = uri,
                                onError = onSecurityError,
                            )
                        },
                    )
                }

                @Deprecated("Deprecated in Android")
                override fun shouldOverrideUrlLoading(
                    view: WebView,
                    url: String,
                ): Boolean =
                    handleNavigation(
                        origin = origin,
                        rawUrl = url,
                        onEpisodeRequested = onEpisodeRequested,
                        onExternal = { uri ->
                            openExternal(
                                activity = context as? Activity,
                                uri = uri,
                                onError = onSecurityError,
                            )
                        },
                    )

                override fun onPageStarted(
                    view: WebView,
                    url: String,
                    favicon: Bitmap?,
                ) {
                    val candidate = runCatching { java.net.URI(url) }.getOrNull()
                    if (candidate != null && !origin.isSameOrigin(candidate)) {
                        view.stopLoading()
                        openExternal(
                            activity = context as? Activity,
                            uri = candidate,
                            onError = onSecurityError,
                        )
                    }
                }

                override fun onPageFinished(
                    view: WebView,
                    url: String,
                ) {
                    CookieManagerCompat.flush()
                    onPageLoaded()
                    val path = runCatching { java.net.URI(url).path }.getOrNull().orEmpty()
                    onLibraryWorkPageChanged(LibraryWebPages.workId(path))
                }

                override fun onReceivedSslError(
                    view: WebView,
                    handler: SslErrorHandler,
                    error: SslError,
                ) {
                    handler.cancel()
                    onSecurityError(
                        "The Jularr server certificate could not be verified. " +
                            "The connection was blocked.",
                    )
                }
            }

            val restored = restoredState?.let { restoreState(it) } != null
            if (!restored) {
                loadUrl(origin.value + "/")
            }
        }
    }

    DisposableEffect(webView) {
        onWebViewChanged(webView)
        onDispose {
            onWebViewChanged(null)
            webView.stopLoading()
            webView.webViewClient = WebViewClient()
            webView.destroy()
        }
    }

    AndroidView(
        factory = { webView },
        modifier = modifier,
    )
}

private fun handleNavigation(
    origin: ServerOrigin,
    rawUrl: String,
    onEpisodeRequested: (String) -> Unit,
    onExternal: (java.net.URI) -> Unit,
): Boolean =
    when (val decision = WebNavigationPolicy.decide(origin, rawUrl)) {
        WebNavigationDecision.AllowInWebView -> false
        is WebNavigationDecision.OpenNativeEpisode -> {
            onEpisodeRequested(decision.episodeId)
            true
        }
        is WebNavigationDecision.OpenExternal -> {
            onExternal(decision.uri)
            true
        }
        WebNavigationDecision.Block -> true
    }

private fun openExternal(
    activity: Activity?,
    uri: java.net.URI,
    onError: (String) -> Unit,
) {
    if (activity == null) {
        onError("No Android activity is available to open the external link.")
        return
    }

    try {
        activity.startActivity(
            Intent(
                Intent.ACTION_VIEW,
                Uri.parse(uri.toString()),
            ),
        )
    } catch (_: ActivityNotFoundException) {
        onError("No Android app can open this external link.")
    }
}

private object CookieManagerCompat {
    fun flush() {
        android.webkit.CookieManager.getInstance().flush()
    }
}
