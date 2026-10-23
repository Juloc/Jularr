package de.juloc.jularr.tv

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.annotation.OptIn
import androidx.activity.compose.setContent
import androidx.media3.common.util.UnstableApi
import androidx.tv.material3.MaterialTheme
import de.juloc.jularr.core.api.HttpJularrClientApi
import de.juloc.jularr.core.player.JularrMedia3Player

@OptIn(UnstableApi::class)
class TvMainActivity : ComponentActivity() {
    private lateinit var player: JularrMedia3Player

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val settings = TvServerSettings(applicationContext)
        val sessionStore = TvSessionStore(applicationContext)
        val cookies = TvSessionCookieStore { updatedCookies ->
            sessionStore.updateCookiesForActiveSession(updatedCookies)
        }

        val controller = TvAppController(
            settings = settings,
            sessionStore = sessionStore,
            cookiesStore = cookies,
        ) { origin ->
            HttpJularrClientApi(
                origin = origin,
                requestHeaders = cookies::requestHeaders,
                responseCookieSink = cookies::accept,
            )
        }

        player = JularrMedia3Player(applicationContext)

        setContent {
            MaterialTheme {
                TvAppHost(
                    controller = controller,
                    settings = settings,
                    cookies = cookies,
                    sessionStore = sessionStore,
                    player = player,
                    onFinish = ::finish,
                )
            }
        }
    }

    override fun onDestroy() {
        if (::player.isInitialized) {
            player.close()
        }
        super.onDestroy()
    }
}
