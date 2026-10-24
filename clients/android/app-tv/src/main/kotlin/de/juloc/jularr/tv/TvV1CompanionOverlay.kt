package de.juloc.jularr.tv

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
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
import com.google.zxing.qrcode.QRCodeWriter

/** The one-time invite is encoded only into a QR fragment; never shown in logs or a GET query. */
@Composable
internal fun TvV1CompanionOverlay(
    url: String?,
    expiresAt: String,
    error: String?,
    busy: Boolean,
    onRenew: () -> Unit,
    onDisconnect: () -> Unit,
    onClose: () -> Unit,
) {
    val bitmap = remember(url) {
        if (url == null) null else runCatching {
            val matrix = QRCodeWriter().encode(url, BarcodeFormat.QR_CODE, 280, 280)
            Bitmap.createBitmap(matrix.width, matrix.height, Bitmap.Config.ARGB_8888).apply {
                for (y in 0 until matrix.height) {
                    for (x in 0 until matrix.width) {
                        setPixel(x, y, if (matrix[x, y]) Color.BLACK else Color.WHITE)
                    }
                }
            }
        }.getOrNull()
    }
    Column(
        modifier = Modifier.width(520.dp)
            .background(MaterialTheme.colorScheme.surface, MaterialTheme.shapes.large)
            .padding(28.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text("Smartphone-Fernbedienung", style = MaterialTheme.typography.headlineMedium)
        if (bitmap != null) {
            Image(bitmap = bitmap.asImageBitmap(), contentDescription = "QR-Code zur Kopplung", modifier = Modifier.size(280.dp))
        } else if (!busy) {
            Text("QR-Code konnte nicht erstellt werden.")
        }
        Text("Öffne den QR-Code auf deinem Smartphone und bestätige die Anmeldung bei Jularr.")
        Text("Einladung gültig bis: $expiresAt", style = MaterialTheme.typography.bodySmall)
        if (busy) Text("Wird verbunden …")
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        Button(enabled = !busy, onClick = onRenew) { Text("Neuen QR-Code erzeugen") }
        Button(enabled = !busy, onClick = onDisconnect) { Text("Fernbedienung trennen") }
        Button(onClick = onClose) { Text("Schließen") }
    }
}
