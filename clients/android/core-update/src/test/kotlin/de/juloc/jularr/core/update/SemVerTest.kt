package de.juloc.jularr.core.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SemVerTest {

    @Test
    fun parsesCoreVersion() {
        val version = SemVer.parse("1.2.3")

        assertEquals(SemVer(1, 2, 3, emptyList()), version)
        assertEquals(false, version?.isPreRelease)
    }

    @Test
    fun parsesLeadingVPrefix() {
        assertEquals(SemVer(0, 1, 0, emptyList()), SemVer.parse("v0.1.0"))
        assertEquals(SemVer(0, 1, 0, emptyList()), SemVer.parse("V0.1.0"))
    }

    @Test
    fun parsesPreReleaseIdentifiers() {
        val version = SemVer.parse("0.1.0-alpha.53")

        assertEquals(SemVer(0, 1, 0, listOf("alpha", "53")), version)
        assertTrue(version!!.isPreRelease)
    }

    @Test
    fun ignoresBuildMetadata() {
        assertEquals(SemVer(1, 0, 0, listOf("alpha")), SemVer.parse("1.0.0-alpha+build.5"))
        assertEquals(SemVer(1, 0, 0, emptyList()), SemVer.parse("1.0.0+build.5"))
    }

    @Test
    fun rejectsUnparsableVersions() {
        assertNull(SemVer.parse("not-a-version"))
        assertNull(SemVer.parse("1.2"))
        assertNull(SemVer.parse(""))
    }

    @Test
    fun higherPatchIsGreater() {
        assertTrue(SemVer.parse("0.1.1")!! > SemVer.parse("0.1.0")!!)
    }

    @Test
    fun higherMinorOutranksPatch() {
        assertTrue(SemVer.parse("0.2.0")!! > SemVer.parse("0.1.9")!!)
    }

    @Test
    fun higherMajorOutranksEverything() {
        assertTrue(SemVer.parse("1.0.0")!! > SemVer.parse("0.99.99")!!)
    }

    @Test
    fun releaseOutranksItsOwnPreRelease() {
        assertTrue(SemVer.parse("1.0.0")!! > SemVer.parse("1.0.0-alpha.1")!!)
        assertTrue(SemVer.parse("1.0.0-alpha.1")!! < SemVer.parse("1.0.0")!!)
    }

    @Test
    fun higherNumericPreReleaseIdentifierIsGreater() {
        assertTrue(SemVer.parse("0.1.0-alpha.53")!! > SemVer.parse("0.1.0-alpha.52")!!)
        assertTrue(SemVer.parse("0.1.0-alpha.9")!! < SemVer.parse("0.1.0-alpha.10")!!)
    }

    @Test
    fun alphabeticPreReleaseIdentifierOutranksNumeric() {
        // semver.org #11: numeric identifiers always have lower precedence than
        // alphanumeric identifiers when compared at the same position.
        assertTrue(SemVer.parse("1.0.0-alpha.beta")!! > SemVer.parse("1.0.0-alpha.9")!!)
    }

    @Test
    fun longerPreReleaseListOutranksAPrefixOfItself() {
        assertTrue(SemVer.parse("1.0.0-alpha.1")!! > SemVer.parse("1.0.0-alpha")!!)
    }

    @Test
    fun equalVersionsCompareEqual() {
        assertEquals(0, SemVer.parse("0.1.0-alpha.53")!!.compareTo(SemVer.parse("0.1.0-alpha.53")!!))
    }
}
