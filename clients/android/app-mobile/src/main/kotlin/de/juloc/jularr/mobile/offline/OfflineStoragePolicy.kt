package de.juloc.jularr.mobile.offline

sealed interface StorageDecision {
    data object Allowed : StorageDecision

    /** The configured device storage limit would be exceeded. */
    data class ExceedsLimit(val requiredBytes: Long, val availableBytes: Long) : StorageDecision

    /** The device itself does not have enough free space (with a safety margin). */
    data class InsufficientDeviceSpace(val requiredBytes: Long, val freeBytes: Long) : StorageDecision
}

object OfflineStoragePolicy {
    private const val GiB = 1024L * 1024L * 1024L

    const val DefaultLimitBytes = 10 * GiB

    /** Free space that downloads never consume, so the device keeps working normally. */
    const val DeviceSafetyMarginBytes = 1 * GiB

    val LimitChoices: List<Long> = listOf(2 * GiB, 5 * GiB, 10 * GiB, 20 * GiB, 50 * GiB, 100 * GiB)

    /**
     * Admission check for a new (or re-queued) download of [requestBytes].
     * [committedBytes] are the committed bytes of all other downloads of the
     * device; [alreadyOnDeviceBytes] is a partial file of this download that
     * will be resumed and therefore needs no additional device space.
     */
    fun admit(
        requestBytes: Long,
        committedBytes: Long,
        limitBytes: Long,
        freeDeviceBytes: Long,
        alreadyOnDeviceBytes: Long = 0,
    ): StorageDecision {
        val availableUnderLimit = (limitBytes - committedBytes).coerceAtLeast(0)
        if (requestBytes > availableUnderLimit) {
            return StorageDecision.ExceedsLimit(requestBytes, availableUnderLimit)
        }

        val remaining = (requestBytes - alreadyOnDeviceBytes).coerceAtLeast(0)
        val usableFree = (freeDeviceBytes - DeviceSafetyMarginBytes).coerceAtLeast(0)
        if (remaining > usableFree) {
            return StorageDecision.InsufficientDeviceSpace(remaining, freeDeviceBytes)
        }

        return StorageDecision.Allowed
    }

    /** Whether writing another [chunkBytes] would eat into the device safety margin. */
    fun hasRoomFor(chunkBytes: Long, freeDeviceBytes: Long): Boolean =
        freeDeviceBytes - chunkBytes >= DeviceSafetyMarginBytes

    fun formatBytes(bytes: Long): String {
        val value = bytes.coerceAtLeast(0).toDouble()
        return when {
            value >= GiB -> "%.1f GB".format(value / GiB)
            value >= 1024 * 1024 -> "%.0f MB".format(value / (1024 * 1024))
            else -> "%.0f KB".format(value / 1024)
        }
    }
}
