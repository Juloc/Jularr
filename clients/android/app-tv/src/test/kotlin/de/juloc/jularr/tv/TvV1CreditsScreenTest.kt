package de.juloc.jularr.tv

import org.junit.Assert.*
import org.junit.Test

class TvV1CreditsScreenTest {
    @Test fun verifiedPersonRoleEnumKeepsMeaning() {
        assertEquals("Schauspiel", tvV1RoleName(1))
        assertEquals("Regie", tvV1RoleName(3))
        assertEquals("Sprecher", tvV1RoleName(11))
        assertEquals("Mitwirkung (Rolle 99)", tvV1RoleName(99))
    }
}
