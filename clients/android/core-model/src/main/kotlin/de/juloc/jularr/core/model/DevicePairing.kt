package de.juloc.jularr.core.model

/**
 * The platform a paired device reports. The server keeps the label with the device session
 * and shows it in the account's session list.
 */
enum class DevicePlatform(val wireValue: String) {
    Android("Android"),
    AndroidTv("AndroidTV"),
}

/**
 * Starts a pairing. The device shows [DevicePairingStarted.code] and keeps the pairing id
 * secret; the id is what fetches the session once a signed-in profile confirmed the code.
 */
data class DevicePairingRequest(
    val deviceName: String?,
    val devicePlatform: DevicePlatform,
    val replacesPairingId: String? = null,
)

data class DevicePairingStarted(
    val pairingId: String,
    val code: String,
    val expiresAt: String,
)

/**
 * The answer of a pairing claim. The device token arrives with the answer that confirms the
 * pairing; a claim before the confirmation carries no token.
 */
data class DevicePairingClaim(
    val isPaired: Boolean,
    val deviceToken: String? = null,
)
