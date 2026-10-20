package de.juloc.jularr.core.design

import org.json.JSONObject

/**
 * The shared design tokens of the product. The web builds its stylesheet from the same file and
 * serves it to the clients, so a client shows the desktop's colors, radii and type scale
 * without carrying a second set of values.
 */
data class DesignTokens(
    val skins: Map<String, DesignSkin>,
    val status: Map<String, DesignStatusColor>,
    val radius: Map<String, Int>,
    val type: Map<String, Int>,
) {
    /** The skin the instance uses, the Clean one when the name is unknown. */
    fun skin(name: String?): DesignSkin = skins[name] ?: skins.getValue("clean")

    /** The status color of an availability state in the given mode. */
    fun statusColor(state: String, dark: Boolean): String? {
        val color = status[state] ?: return null
        return if (dark) color.dark else color.light
    }

    companion object {
        /** Reads the tokens the instance serves. */
        fun parse(json: String): DesignTokens {
            val root = JSONObject(json)
            val skins = root.getJSONObject("skins").keys().asSequence().associateWith { name ->
                val skin = root.getJSONObject("skins").getJSONObject(name)
                DesignSkin(
                    accent = skin.getString("accent"),
                    shadow = skin.getString("shadow"),
                    light = skin.getJSONObject("palette").getJSONObject("light").toStringMap(),
                    dark = skin.getJSONObject("palette").getJSONObject("dark").toStringMap(),
                )
            }
            val status = root.getJSONObject("status").keys().asSequence().associateWith { name ->
                val color = root.getJSONObject("status").getJSONObject(name)
                DesignStatusColor(light = color.getString("light"), dark = color.getString("dark"))
            }
            return DesignTokens(
                skins = skins,
                status = status,
                radius = root.getJSONObject("radius").toIntMap(),
                type = root.getJSONObject("type").toIntMap(),
            )
        }

        private fun JSONObject.toStringMap(): Map<String, String> =
            keys().asSequence().associateWith { getString(it) }

        private fun JSONObject.toIntMap(): Map<String, Int> =
            keys().asSequence().associateWith { getInt(it) }
    }
}

/** One skin: its accent, its shadow and the palette per mode. */
data class DesignSkin(
    val accent: String,
    val shadow: String,
    val light: Map<String, String>,
    val dark: Map<String, String>,
) {
    fun color(name: String, dark: Boolean): String? = (if (dark) this.dark else this.light)[name]
}

/** One status color, the one the web shows for an availability state. */
data class DesignStatusColor(
    val light: String,
    val dark: String,
)
