package de.juloc.jularr.tv

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.tv.material3.MaterialTheme

/**
 * The clean-start entry point starts with device pairing, not the legacy cookie login.
 * The original Compose screens and Media3 player are routed through the active V1 catalog host.
 */
class TvMainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val settings = TvServerSettings(applicationContext)
        val sessionStore = TvSessionStore(applicationContext)
        val deviceSession = TvV1DeviceSession(sessionStore)

        setContent {
            MaterialTheme {
                TvV1EntryHost(settings, deviceSession) {
                        origin, capabilities, onExpired, onProfileChanged, onSignOut, onChangeServer, onSwitchAccount ->
                    TvV1CatalogHost(
                        origin, capabilities, deviceSession, onExpired,
                        onProfileChanged, onSignOut, onChangeServer, onSwitchAccount,
                    )
                }
            }
        }
    }
}
