package de.juloc.jularr.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import coil3.compose.AsyncImage

/**
 * The details of a title: the backdrop, the name, the year, the genres and the text of the
 * instance. The actions of the title come with the player.
 */
@Composable
fun TvDetailsScreen(controller: TvController) {
    val colors = LocalTvColors.current
    val type = LocalTvType.current
    val details = controller.details
    if (details == null) {
        Text(
            text = controller.texts["tv.details.loading"],
            fontSize = type.body,
            color = colors.muted,
        )
        return
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // The backdrop of the desktop.
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(TvLayout.HeroHeight)
                .clip(RoundedCornerShape(TvLayout.HeroRadius))
                .background(colors.field),
        ) {
            if (details.backdropUrl != null) {
                AsyncImage(
                    model = details.backdropUrl,
                    contentDescription = details.canonicalTitle,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }
        Spacer(Modifier.height(TvLayout.Gap))
        Text(
            text = details.canonicalTitle,
            fontSize = type.heroTitle,
            fontWeight = FontWeight.Bold,
            color = colors.text,
        )
        Spacer(Modifier.height(4.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(TvLayout.Gap)) {
            details.releaseYear?.let { year ->
                Text(text = year.toString(), fontSize = type.body, color = colors.muted)
            }
            details.genres?.let { genres ->
                Text(text = genres, fontSize = type.body, color = colors.muted)
            }
            details.studio?.let { studio ->
                Text(text = studio, fontSize = type.body, color = colors.muted)
            }
        }
        Spacer(Modifier.height(TvLayout.Gap))
        Text(
            text = details.overview.orEmpty(),
            fontSize = type.body,
            color = colors.text,
            modifier = Modifier.width(TvLayout.CardWidth * 4),
        )
    }
}
