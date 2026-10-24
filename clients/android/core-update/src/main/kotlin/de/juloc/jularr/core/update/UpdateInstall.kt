package de.juloc.jularr.core.update

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import java.io.File

/**
 * Hands a verified APK file to Android's own package-installer confirmation flow. This
 * never installs silently: the user still confirms the install prompt Android shows.
 * The caller must have already verified the file (see [Sha256]/[UpdateManager]) before
 * calling [installIntent].
 */
object UpdateInstall {

    /** `ACTION_VIEW` on a `FileProvider` URI for [apkFile], typed as an APK for the installer. */
    fun installIntent(context: Context, apkFile: File, authority: String): Intent {
        val uri = FileProvider.getUriForFile(context.applicationContext, authority, apkFile)
        return Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
    }

    /** Whether this app is currently allowed to request package installs (Android O+). */
    fun canInstallPackages(context: Context): Boolean =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.O ||
            context.packageManager.canRequestPackageInstalls()

    /** Opens the system settings screen where the user grants "install unknown apps". */
    fun manageUnknownAppSourcesIntent(context: Context): Intent =
        Intent(
            Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
            Uri.parse("package:${context.packageName}"),
        )
}
