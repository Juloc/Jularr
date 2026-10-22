package de.juloc.jularr.core.model

/**
 * The caller's verified grants and the instance modules that work for it. The server is the
 * only owner of both; a client hides a module it may not use, it never decides that itself.
 */
data class InstanceCapabilities(
    val permissions: List<String>,
    val modules: List<String>,
) {
    fun hasModule(name: String): Boolean = modules.contains(name)

    fun hasPermission(name: String): Boolean = permissions.contains(name)
}
