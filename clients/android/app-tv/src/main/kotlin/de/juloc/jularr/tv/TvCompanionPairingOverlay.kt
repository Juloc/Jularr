package de.juloc.jularr.tv

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.unit.dp
import androidx.tv.material3.Button
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import de.juloc.jularr.core.session.PlaybackPairing

@Composable
fun TvCompanionPairingOverlay(
    pairing: PlaybackPairing,
    companionUrl: String,
    busy: Boolean,
    onNewCode: () -> Unit,
    onRevoke: () -> Unit,
    onClose: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val qr = remember(companionUrl) {
        companionQrBitmap(companionUrl, size = 420)
    }

    Column(
        modifier = modifier
            .fillMaxWidth(0.78f)
            .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.98f))
            .padding(36.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(18.dp),
    ) {
        Text(
            text = "Open on phone",
            style = MaterialTheme.typography.headlineMedium,
        )
        Text(
            text = "Scan the QR code or enter this code on /Companion",
            style = MaterialTheme.typography.bodyLarge,
        )
        Image(
            bitmap = qr.asImageBitmap(),
            contentDescription = "Companion pairing QR code",
            modifier = Modifier.size(280.dp),
        )
        Text(
            text = pairing.code,
            style = MaterialTheme.typography.displayMedium,
        )
        Text(
            text = "The code and QR token expire after 5 minutes and are single-use.",
            style = MaterialTheme.typography.bodyMedium,
        )
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(
                onClick = onNewCode,
                enabled = !busy,
            ) {
                Text("New code")
            }
            Button(
                onClick = onRevoke,
                enabled = !busy,
            ) {
                Text("Revoke phones")
            }
            Button(onClick = onClose) {
                Text("Close")
            }
        }
    }
}

internal fun companionQrBitmap(
    value: String,
    size: Int,
): Bitmap {
    require(value.isNotBlank()) { "QR value must not be blank." }
    require(size > 0) { "QR size must be positive." }

    val matrix = QRCodeWriter().encode(
        value,
        BarcodeFormat.QR_CODE,
        size,
        size,
        mapOf(EncodeHintType.MARGIN to 1),
    )

    val pixels = IntArray(size * size)
    for (y in 0 until size) {
        for (x in 0 until size) {
            pixels[y * size + x] =
                if (matrix[x, y]) 0xFF000000.toInt() else 0xFFFFFFFF.toInt()
        }
    }

    return Bitmap.createBitmap(
        pixels,
        size,
        size,
        Bitmap.Config.ARGB_8888,
    )
}
