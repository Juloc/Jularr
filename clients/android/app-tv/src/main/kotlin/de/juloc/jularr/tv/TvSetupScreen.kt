package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp

/**
 * The screen before the instance answered: the address of the instance and the pairing code
 * the account confirms.
 */
@Composable
fun TvSetupScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    var address by remember { mutableStateOf(controller.address) }
    val addressFocus = remember { FocusRequester() }

    // The address is the first thing the television asks for, so it holds the focus.
    LaunchedEffect(Unit) { addressFocus.requestFocus() }

    CompositionLocalProvider(LocalTvType provides type) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = TvLayout.ContentPadding * 3, vertical = TvLayout.ContentPadding * 2),
            verticalArrangement = Arrangement.spacedBy(TvLayout.Gap),
        ) {
            Text(
                text = "Jularr",
                fontSize = type.heroTitle,
                fontWeight = FontWeight.ExtraBold,
                color = colors.text,
            )
            Text(
                text = controller.texts["tv.address"],
                fontSize = type.body,
                color = colors.muted,
            )
            OutlinedTextField(
                value = address,
                onValueChange = { address = it },
                singleLine = true,
                label = { Text(controller.texts["tv.address"], fontSize = type.body) },
                textStyle = androidx.compose.ui.text.TextStyle(fontSize = type.body, color = colors.text),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                keyboardActions = KeyboardActions(onDone = { controller.connect(address) }),
                modifier = Modifier.width(420.dp).focusRequester(addressFocus),
            )
            Button(
                onClick = { controller.connect(address) },
                enabled = !controller.busy,
                shape = RoundedCornerShape(TvLayout.RailItemRadius),
                colors = ButtonDefaults.buttonColors(
                    containerColor = colors.accent,
                    contentColor = colors.accentContrast,
                ),
                modifier = Modifier.height(TvLayout.RailItemHeight),
            ) {
                Text(controller.texts["tv.connect"], fontSize = type.body)
            }

            // The pairing: the code the account confirms.
            if (controller.phase == TvPhase.Pairing && controller.pairingCode != null) {
                Spacer(Modifier.height(TvLayout.Gap))
                Text(
                    text = controller.texts["tv.pair.waiting"],
                    fontSize = type.body,
                    color = colors.muted,
                )
                Box(
                    modifier = Modifier
                        .width(420.dp)
                        .height(90.dp)
                        .background(colors.surface, RoundedCornerShape(TvLayout.TopRowRadius))
                        .border(1.dp, colors.border, RoundedCornerShape(TvLayout.TopRowRadius)),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = controller.pairingCode.orEmpty(),
                        fontSize = type.heroTitle,
                        fontWeight = FontWeight.Bold,
                        color = colors.accentText,
                        textAlign = TextAlign.Center,
                    )
                }
            }

            controller.error?.let { failure ->
                Text(
                    text = failure,
                    fontSize = type.body,
                    color = colors.danger,
                )
            }
        }
    }
}
