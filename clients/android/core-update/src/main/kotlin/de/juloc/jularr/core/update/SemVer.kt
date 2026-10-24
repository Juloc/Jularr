package de.juloc.jularr.core.update

/**
 * Minimal semantic-version parser and comparator (semver.org precedence rules), used to
 * compare the installed app's `BuildConfig.VERSION_NAME` against a GitHub release
 * version. Only precedence matters here; build metadata (a trailing `+...`) is ignored
 * as the spec requires, and a leading `v`/`V` (as used in the release git tag) is
 * tolerated.
 */
data class SemVer(
    val major: Int,
    val minor: Int,
    val patch: Int,
    val preRelease: List<String>,
) : Comparable<SemVer> {

    val isPreRelease: Boolean get() = preRelease.isNotEmpty()

    override fun compareTo(other: SemVer): Int {
        major.compareTo(other.major).let { if (it != 0) return it }
        minor.compareTo(other.minor).let { if (it != 0) return it }
        patch.compareTo(other.patch).let { if (it != 0) return it }

        return when {
            preRelease.isEmpty() && other.preRelease.isEmpty() -> 0
            // A pre-release version has lower precedence than the associated normal version.
            preRelease.isEmpty() -> 1
            other.preRelease.isEmpty() -> -1
            else -> comparePreRelease(other)
        }
    }

    private fun comparePreRelease(other: SemVer): Int {
        val length = minOf(preRelease.size, other.preRelease.size)
        for (index in 0 until length) {
            val comparison = compareIdentifier(preRelease[index], other.preRelease[index])
            if (comparison != 0) return comparison
        }
        // A larger set of pre-release fields has higher precedence when all preceding
        // identifiers are equal.
        return preRelease.size.compareTo(other.preRelease.size)
    }

    override fun toString(): String = buildString {
        append(major).append('.').append(minor).append('.').append(patch)
        if (preRelease.isNotEmpty()) {
            append('-').append(preRelease.joinToString("."))
        }
    }

    companion object {
        private val corePattern = Regex("""^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?.*$""")

        fun parse(raw: String): SemVer? {
            val withoutPrefix = raw.trim().removePrefix("v").removePrefix("V")
            val withoutBuildMetadata = withoutPrefix.substringBefore('+')
            val match = corePattern.find(withoutBuildMetadata) ?: return null
            val (majorText, minorText, patchText, preReleaseText) = match.destructured

            val major = majorText.toIntOrNull() ?: return null
            val minor = minorText.toIntOrNull() ?: return null
            val patch = patchText.toIntOrNull() ?: return null
            val preRelease = if (preReleaseText.isEmpty()) emptyList() else preReleaseText.split('.')

            return SemVer(major, minor, patch, preRelease)
        }

        /** Numeric identifiers are compared numerically and always have lower precedence
         * than alphanumeric identifiers with the same preceding fields (semver.org §11). */
        private fun compareIdentifier(left: String, right: String): Int {
            val leftNumeric = left.toLongOrNull()
            val rightNumeric = right.toLongOrNull()
            return when {
                leftNumeric != null && rightNumeric != null -> leftNumeric.compareTo(rightNumeric)
                leftNumeric != null -> -1
                rightNumeric != null -> 1
                else -> left.compareTo(right)
            }
        }
    }
}
