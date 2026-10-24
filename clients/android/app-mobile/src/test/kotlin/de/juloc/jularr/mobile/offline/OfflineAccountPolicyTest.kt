package de.juloc.jularr.mobile.offline

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class OfflineAccountPolicyTest {
    private val origin = "https://jularr.example"

    @Test
    fun firstSignInIsAdopted() {
        assertEquals(
            AccountDecision.Adopt(OfflineAccount(origin, "a", signedIn = true)),
            OfflineAccountPolicy.decide(null, AccountCheck.SignedIn(origin, "a")),
        )
    }

    @Test
    fun logoutLocksAndTheSameAccountUnlocksAgain() {
        val current = OfflineAccount(origin, "a", signedIn = true)
        val locked = OfflineAccountPolicy.decide(current, AccountCheck.SignedOut(origin))
        assertEquals(AccountDecision.Lock(current.copy(signedIn = false)), locked)

        assertEquals(
            AccountDecision.Adopt(current),
            OfflineAccountPolicy.decide(current.copy(signedIn = false), AccountCheck.SignedIn(origin, "a")),
        )
    }

    @Test
    fun anotherAccountPurgesThePreviousDownloads() {
        val current = OfflineAccount(origin, "a", signedIn = false)

        assertEquals(
            AccountDecision.SwitchAndPurge(OfflineAccount(origin, "b", signedIn = true), current.ownerKey),
            OfflineAccountPolicy.decide(current, AccountCheck.SignedIn(origin, "b")),
        )
    }

    @Test
    fun anotherServerIsAnotherOwner() {
        val current = OfflineAccount(origin, "a", signedIn = true)
        val decision = OfflineAccountPolicy.decide(current, AccountCheck.SignedIn("https://other.example", "a"))

        assertEquals(current.ownerKey, (decision as AccountDecision.SwitchAndPurge).previousOwnerKey)
        assertNotEquals(OfflineOwner.key(origin, "a"), OfflineOwner.key("https://other.example", "a"))
    }

    @Test
    fun repeatedChecksChangeNothing() {
        val current = OfflineAccount(origin, "a", signedIn = true)

        assertEquals(AccountDecision.Keep, OfflineAccountPolicy.decide(current, AccountCheck.SignedIn(origin, "a")))
        assertEquals(AccountDecision.Keep, OfflineAccountPolicy.decide(null, AccountCheck.SignedOut(origin)))
        assertEquals(
            AccountDecision.Keep,
            OfflineAccountPolicy.decide(current.copy(signedIn = false), AccountCheck.SignedOut(origin)),
        )
    }

    @Test
    fun accessibleDownloadsRequireTheSignedInOwner() {
        val account = OfflineAccount(origin, "profile-a", signedIn = true)
        val mine = sampleDownload()
        val other = mine.copy(ownerKey = OfflineOwner.key(origin, "profile-b"), episodeId = "other")
        val snapshot = OfflineSnapshot(account = account, downloads = listOf(mine, other))

        assertEquals(listOf(mine), snapshot.accessibleDownloads(origin))
        assertEquals(emptyList<OfflineDownload>(), snapshot.accessibleDownloads("https://other.example"))
        assertEquals(
            emptyList<OfflineDownload>(),
            snapshot.copy(account = account.copy(signedIn = false)).accessibleDownloads(origin),
        )
    }
}
