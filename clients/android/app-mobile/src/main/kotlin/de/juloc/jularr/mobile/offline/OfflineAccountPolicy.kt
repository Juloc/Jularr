package de.juloc.jularr.mobile.offline

sealed interface AccountCheck {
    data class SignedIn(val origin: String, val profileId: String) : AccountCheck
    data class SignedOut(val origin: String) : AccountCheck
}

sealed interface AccountDecision {
    /** Nothing changes. */
    data object Keep : AccountDecision

    /** Record [account] as the accessible account; no other account's downloads exist. */
    data class Adopt(val account: OfflineAccount) : AccountDecision

    /** Signed out: keep the files but make them inaccessible until the same account signs in again. */
    data class Lock(val account: OfflineAccount) : AccountDecision

    /** A different account or server is now active: delete everything of [previousOwnerKey]. */
    data class SwitchAndPurge(val account: OfflineAccount, val previousOwnerKey: String) : AccountDecision
}

/**
 * Account boundary for managed downloads. Downloads belong to exactly one
 * account on one server. Logging out makes them inaccessible; signing in as
 * another account (or on another server) removes them.
 */
object OfflineAccountPolicy {
    fun decide(current: OfflineAccount?, check: AccountCheck): AccountDecision =
        when (check) {
            is AccountCheck.SignedOut -> when {
                current == null || current.origin != check.origin || !current.signedIn -> AccountDecision.Keep
                else -> AccountDecision.Lock(current.copy(signedIn = false))
            }

            is AccountCheck.SignedIn -> {
                val next = OfflineAccount(check.origin, check.profileId, signedIn = true)
                when {
                    current == null -> AccountDecision.Adopt(next)
                    current.ownerKey != next.ownerKey -> AccountDecision.SwitchAndPurge(next, current.ownerKey)
                    current.signedIn -> AccountDecision.Keep
                    else -> AccountDecision.Adopt(next)
                }
            }
        }
}
