package de.juloc.jularr.core.design

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test

class DesignTokensTest {
    private val json = """
        {
          "skins": {
            "clean": {
              "accent": "#7c3aed",
              "shadow": "0 2px 8px light-dark(rgb(32 19 58 / 4%), rgb(0 0 0 / 14%))",
              "palette": {
                "light": { "background": "#f6f5f9", "surface": "#ffffff", "text": "#25202f", "accent": "#7c3aed" },
                "dark": { "background": "#141319", "surface": "#1d1b24", "text": "#f4f0f8", "accent": "#7c3aed" }
              }
            },
            "original": {
              "accent": "#c8102e",
              "shadow": "0 3px 14px light-dark(rgb(70 35 30 / 6%), rgb(0 0 0 / 20%))",
              "palette": {
                "light": { "background": "#f6f1e9", "surface": "#fffdf9", "text": "#302426", "accent": "#c8102e" },
                "dark": { "background": "#191619", "surface": "#242024", "text": "#f5ece6", "accent": "#c8102e" }
              }
            }
          },
          "status": {
            "available": { "light": "#16a34a", "dark": "#22c55e" },
            "missing": { "light": "#dc3545", "dark": "#fb5969" }
          },
          "radius": { "control": 999, "panel": 12 },
          "type": { "body": 14, "cardTitle": 14 }
        }
    """.trimIndent()

    @Test
    fun theSkinsCarryTheirPalettePerMode() {
        val tokens = DesignTokens.parse(json)

        assertEquals("#7c3aed", tokens.skin("clean").accent)
        assertEquals("#141319", tokens.skin("clean").color("background", true))
        assertEquals("#f6f5f9", tokens.skin("clean").color("background", false))
        assertEquals("#c8102e", tokens.skin("original").accent)
    }

    @Test
    fun anUnknownSkinFallsBackToTheCleanOne() {
        val tokens = DesignTokens.parse(json)

        assertEquals("#7c3aed", tokens.skin("unknown").accent)
        assertEquals("#7c3aed", tokens.skin(null).accent)
    }

    @Test
    fun theStatusColorsAndSizesAreRead() {
        val tokens = DesignTokens.parse(json)

        assertEquals("#22c55e", tokens.statusColor("available", true))
        assertEquals("#dc3545", tokens.statusColor("missing", false))
        assertEquals(null, tokens.statusColor("unknown", false))
        assertEquals(999, tokens.radius["control"])
        assertEquals(14, tokens.type["body"])
    }
}
