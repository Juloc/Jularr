using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Jularr.Integrations.Logic.Storage
{
    // Places completed downloads into a library: hardlink on the same filesystem, otherwise a verified copy.
    internal static class StorageFileTransfer
    {
        public static void HardlinkOrCopy(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                throw new IOException("The library already contains a file at the destination.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (TryHardLink(sourcePath, destinationPath))
            {
                return;
            }

            // Copy beside the target and publish it only when complete.
            var partial = destinationPath + ".jularr-partial";
            try
            {
                File.Copy(sourcePath, partial, overwrite: true);
                if (new FileInfo(partial).Length != new FileInfo(sourcePath).Length)
                {
                    throw new IOException("The copied file is incomplete.");
                }

                File.Move(partial, destinationPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }

        private static bool TryHardLink(string sourcePath, string destinationPath)
        {
            int error;
            if (OperatingSystem.IsWindows())
            {
                if (Windows.CreateHardLinkW(destinationPath, sourcePath, IntPtr.Zero)) return true;
                error = Marshal.GetLastWin32Error();

                // ERROR_NOT_SAME_DEVICE, ERROR_INVALID_FUNCTION or unsupported file system.
                if (error is 17 or 1 or 50) return false;
            }
            else
            {
                if (Unix.link(sourcePath, destinationPath) == 0) return true;
                error = Marshal.GetLastPInvokeError();

                // EXDEV, EPERM or ENOTSUP: links are not possible here.
                if (error is 18 or 1 or 95) return false;
            }

            throw new IOException("Could not link the file: " + new Win32Exception(error).Message);
        }

        [SupportedOSPlatform("windows")]
        private static class Windows
        {
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
        }

        private static class Unix
        {
            [DllImport("libc", SetLastError = true, EntryPoint = "link")]
            public static extern int link(string oldpath, string newpath);
        }
    }
}
