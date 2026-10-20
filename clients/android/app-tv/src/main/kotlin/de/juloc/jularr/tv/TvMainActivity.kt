package de.juloc.jularr.tv

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.addCallback
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier

/** The entry point of the television app. */
class TvMainActivity : ComponentActivity() {
    private lateinit var controller: TvController

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val controller = TvController(applicationContext)
        this.controller = controller
        setContent {
            TvApp(controller)
        }
        // Back returns to the surface the television came from.
        onBackPressedDispatcher.addCallback(this) {
            if (!controller.back()) {
                finish()
            }
        }
        controller.start()
    }
}

/** The app: the setup, the pairing and the shell with the library. */
@Composable
fun TvApp(controller: TvController) {
    val tokens = controller.designTokens
    val colors = tokens?.colors(controller.skin, controller.dark) ?: neutralColors()
    CompositionLocalProvider(
        LocalTvColors provides colors,
        LocalTvType provides rememberTvType(tokens),
    ) {
        Surface(
            color = colors.background,
            modifier = Modifier.fillMaxSize(),
        ) {
            when (controller.phase) {
                // The setup carries the pairing: the code appears under the address.
                TvPhase.Setup, TvPhase.Pairing -> TvSetupScreen(controller)
                TvPhase.Ready -> TvShell(controller)
            }
        }
    }
}
