package de.juloc.jularr.mobile

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import de.juloc.jularr.core.api.HttpJularrClientApi
import de.juloc.jularr.core.model.SpeechModel
import de.juloc.jularr.core.model.TtsPreferences
import de.juloc.jularr.core.model.TtsPreferencesUpdate
import de.juloc.jularr.core.tts.model.InstalledTtsModel
import de.juloc.jularr.core.tts.model.SpeechModelFile
import de.juloc.jularr.core.tts.model.TtsModelDownloadResult
import de.juloc.jularr.core.tts.model.TtsModelFileDownloader
import de.juloc.jularr.core.tts.model.TtsModelManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * Native TTS settings: provider choice, rate/pitch/volume and offline-neural model
 * management. Profile-level preferences sync through `/me/tts-preferences` (the same
 * canonical store the web Reader's Vorlesen settings use); installed models are a
 * device-only fact and never leave the device (docs/TTS.md).
 */
@Composable
fun TtsSettingsScreen(
    api: HttpJularrClientApi,
    onClose: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val modelManager = remember {
        TtsModelManager(
            modelsRoot = File(context.filesDir, "tts-models"),
            downloader = TtsModelFileDownloader { file, destination -> downloadToFile(file, destination) },
        )
    }

    var preferences by remember { mutableStateOf<TtsPreferences?>(null) }
    var availableModels by remember { mutableStateOf<List<SpeechModel>>(emptyList()) }
    var installedModels by remember { mutableStateOf<List<InstalledTtsModel>>(modelManager.installedModels()) }
    var downloadingModelId by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }

    LaunchedEffect(Unit) {
        loading = true
        error = null
        try {
            preferences = withContext(Dispatchers.IO) { api.getTtsPreferences() }
            availableModels = withContext(Dispatchers.IO) { api.getSpeechModels().models }
        } catch (exception: Exception) {
            error = exception.message ?: "Could not load speech settings."
        } finally {
            loading = false
        }
    }

    fun updatePreferences(update: TtsPreferencesUpdate) {
        scope.launch {
            try {
                preferences = withContext(Dispatchers.IO) { api.updateTtsPreferences(update) }
            } catch (exception: Exception) {
                error = exception.message ?: "Could not save speech settings."
            }
        }
    }

    Surface(modifier = Modifier.fillMaxSize()) {
        Column(modifier = Modifier.padding(20.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                Text("Speech settings", style = MaterialTheme.typography.headlineSmall)
                TextButton(onClick = onClose) { Text("Close") }
            }

            if (loading) {
                CircularProgressIndicator(modifier = Modifier.padding(top = 16.dp))
                return@Column
            }

            error?.let {
                Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 8.dp))
            }

            preferences?.let { current ->
                Text(
                    "Provider",
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.padding(top = 16.dp),
                )
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf("auto" to "Automatic", "device" to "Device").forEach { (id, label) ->
                        val selected = current.providerId == id
                        Button(
                            onClick = { updatePreferences(TtsPreferencesUpdate(providerId = id)) },
                            enabled = !selected,
                        ) {
                            Text(label)
                        }
                    }
                }

                LabeledSlider(
                    label = "Rate",
                    value = current.rate,
                    range = 0.5f..2.5f,
                    onValueChangeFinished = { updatePreferences(TtsPreferencesUpdate(rate = it.toDouble())) },
                )
                LabeledSlider(
                    label = "Pitch",
                    value = current.pitch,
                    range = 0.5f..2.0f,
                    onValueChangeFinished = { updatePreferences(TtsPreferencesUpdate(pitch = it.toDouble())) },
                )
                LabeledSlider(
                    label = "Volume",
                    value = current.volume,
                    range = 0.0f..1.0f,
                    onValueChangeFinished = { updatePreferences(TtsPreferencesUpdate(volume = it.toDouble())) },
                )
            }

            Text(
                "Offline neural models",
                style = MaterialTheme.typography.titleMedium,
                modifier = Modifier.padding(top = 24.dp),
            )
            Text(
                "Downloaded once, verified by checksum, and spoken fully on this device with " +
                    "no network request. Device voices above may be vendor/network-backed; only " +
                    "these are guaranteed offline.",
                style = MaterialTheme.typography.bodySmall,
            )

            if (availableModels.isEmpty()) {
                Text(
                    "No offline model is configured on this server yet.",
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 8.dp),
                )
            }

            LazyColumn(modifier = Modifier.padding(top = 8.dp)) {
                items(availableModels, key = { it.modelId }) { model ->
                    val installed = installedModels.firstOrNull { it.modelId == model.modelId }
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                        horizontalArrangement = Arrangement.SpaceBetween,
                    ) {
                        Column {
                            Text("${model.providerId} / ${model.modelId} (${model.version})")
                            Text(
                                model.languages.joinToString(", "),
                                style = MaterialTheme.typography.bodySmall,
                            )
                        }

                        when {
                            downloadingModelId == model.modelId ->
                                LinearProgressIndicator(modifier = Modifier.padding(start = 8.dp))
                            installed != null -> TextButton(onClick = {
                                modelManager.delete(model.modelId)
                                installedModels = modelManager.installedModels()
                            }) { Text("Delete") }
                            else -> TextButton(onClick = {
                                scope.launch {
                                    downloadingModelId = model.modelId
                                    val entry = de.juloc.jularr.core.tts.model.SpeechModelManifestEntry(
                                        providerId = model.providerId,
                                        modelId = model.modelId,
                                        version = model.version,
                                        languages = model.languages,
                                        voices = model.voices,
                                        files = model.files.map {
                                            SpeechModelFile(it.name, it.url, it.sizeBytes, it.sha256)
                                        },
                                        totalSizeBytes = model.totalSizeBytes,
                                        minimumCompatibleVersion = model.minimumCompatibleVersion,
                                    )
                                    val result = withContext(Dispatchers.IO) { modelManager.download(entry) }
                                    downloadingModelId = null
                                    if (result !is TtsModelDownloadResult.Success) {
                                        error = when (result) {
                                            is TtsModelDownloadResult.ChecksumMismatch ->
                                                "Download failed checksum verification (${result.fileName})."
                                            is TtsModelDownloadResult.DownloadFailed -> result.message
                                            else -> "Download failed."
                                        }
                                    }
                                    installedModels = modelManager.installedModels()
                                }
                            }) { Text("Download") }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun LabeledSlider(
    label: String,
    value: Double,
    range: ClosedFloatingPointRange<Float>,
    onValueChangeFinished: (Float) -> Unit,
) {
    var sliderValue by remember(value) { mutableStateOf(value.toFloat()) }
    Column(modifier = Modifier.padding(top = 12.dp)) {
        Text("$label: ${"%.2f".format(sliderValue)}", style = MaterialTheme.typography.bodyMedium)
        Slider(
            value = sliderValue,
            valueRange = range,
            onValueChange = { sliderValue = it },
            onValueChangeFinished = { onValueChangeFinished(sliderValue) },
        )
    }
}

private fun downloadToFile(file: SpeechModelFile, destination: File) {
    val connection = URL(file.url).openConnection() as HttpURLConnection
    connection.connectTimeout = 15_000
    connection.readTimeout = 60_000
    try {
        connection.inputStream.use { input ->
            destination.parentFile?.mkdirs()
            destination.outputStream().use { output -> input.copyTo(output) }
        }
    } finally {
        connection.disconnect()
    }
}
