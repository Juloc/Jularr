using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Jularr.Integrations.Logic.Browser
{
    // The X virtual framebuffer the source browser draws on when the host has no display of its own.
    // It lives as long as the browser; its lock and socket go with it, also those of a run that crashed.
    internal sealed class VirtualDisplay : IDisposable
    {
        // A normal desktop screen that the browser window of 1280 by 900 fills.
        internal const string Screen = "1280x900x24";

        // The file in the profile that names the display of the current run.
        internal const string RecordFile = "browser.display";

        private const int FirstNumber = 99;
        private const int LastNumber = 160;
        private const int StartMilliseconds = 5000;

        private readonly Process _process;
        private readonly string _root;
        private readonly string _record;

        private VirtualDisplay(Process process, int number, string root, string record)
        {
            _process = process;
            Number = number;
            _root = root;
            _record = record;
        }

        public int Number { get; }
        public string Name => ":" + Number.ToString(CultureInfo.InvariantCulture);

        // One display on the first free number; null when Xvfb is missing or does not come up.
        // The root is where X servers keep their locks and sockets, /tmp outside of tests.
        internal static async Task<VirtualDisplay?> StartAsync(string? executable, string root, string record, CancellationToken ct)
        {
            if (executable is null || !File.Exists(executable))
            {
                return null;
            }

            StopRecorded(executable, root, record);
            for (var number = FirstNumber; number <= LastNumber; number++)
            {
                if (!IsFree(root, number))
                {
                    continue;
                }

                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { ":" + number.ToString(CultureInfo.InvariantCulture), "-screen", "0", Screen, "-nolisten", "tcp" }
                };
                Process? process;
                try
                {
                    process = Process.Start(start);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    return null;
                }

                if (process is null)
                {
                    return null;
                }

                // The server's own notes are not the host's log.
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // The display is ready when its socket is there; a server that leaves met a number in use.
                var deadline = DateTime.UtcNow.AddMilliseconds(StartMilliseconds);
                try
                {
                    while (!process.HasExited && !File.Exists(Socket(root, number)) && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(100, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    Stop(process);
                    RemoveFiles(root, number, process.Id);
                    process.Dispose();
                    throw;
                }

                if (!process.HasExited && File.Exists(Socket(root, number)))
                {
                    var display = new VirtualDisplay(process, number, root, record);
                    try
                    {
                        await File.WriteAllTextAsync(record, number.ToString(CultureInfo.InvariantCulture) + " " + process.Id.ToString(CultureInfo.InvariantCulture), ct);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Without the note a crash leaves this display to the next free number.
                    }

                    return display;
                }

                var leftByItself = process.HasExited;
                Stop(process);
                RemoveFiles(root, number, process.Id);
                process.Dispose();
                if (!leftByItself)
                {
                    return null;
                }
            }

            return null;
        }

        // The server goes with the browser; a killed server leaves its lock and socket behind.
        public void Dispose()
        {
            Stop(_process);
            RemoveFiles(_root, Number, _process.Id);
            try
            {
                File.Delete(_record);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The next start finds no server behind the note.
            }

            _process.Dispose();
        }

        // A number is free without a lock or with the lock of a server that is gone.
        private static bool IsFree(string root, int number)
        {
            var owner = LockOwner(root, number);
            if (owner is null)
            {
                return !File.Exists(Lock(root, number));
            }

            if (IsDisplayServer(owner.Value))
            {
                return false;
            }

            RemoveFiles(root, number, owner.Value);
            return !File.Exists(Lock(root, number));
        }

        // The display of a run that ended without closing it still holds its number.
        private static void StopRecorded(string executable, string root, string record)
        {
            try
            {
                if (!File.Exists(record))
                {
                    return;
                }

                var parts = File.ReadAllText(record).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                File.Delete(record);
                if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var number) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var id))
                {
                    return;
                }

                // Only the server that still holds that lock under its own name is ours.
                if (LockOwner(root, number) == id && Command(id) is { } command && Path.GetFileName(executable).StartsWith(command, StringComparison.Ordinal))
                {
                    using var process = Process.GetProcessById(id);
                    Stop(process);
                }

                if (!IsDisplayServer(id))
                {
                    RemoveFiles(root, number, id);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // The earlier server is gone or not ours.
            }
        }

        private static void Stop(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or SystemException)
            {
                // The server is already gone.
            }
        }

        // The lock and socket of one server, never those of another that took the number since.
        private static void RemoveFiles(string root, int number, int id)
        {
            var owner = LockOwner(root, number);
            if (owner is not null && owner != id)
            {
                return;
            }

            try
            {
                File.Delete(Lock(root, number));
                File.Delete(Socket(root, number));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The next start takes another number.
            }
        }

        // After a container restart the number of a gone server often belongs to another process.
        private static bool IsDisplayServer(int id) => Command(id)?.StartsWith('X') == true;

        private static string? Command(int id)
        {
            try
            {
                var file = $"/proc/{id.ToString(CultureInfo.InvariantCulture)}/comm";
                return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static int? LockOwner(string root, int number)
        {
            try
            {
                var text = File.Exists(Lock(root, number)) ? File.ReadAllText(Lock(root, number)).Trim() : "";
                return int.TryParse(text, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string Lock(string root, int number) => Path.Combine(root, $".X{number}-lock");

        private static string Socket(string root, int number) => Path.Combine(root, ".X11-unix", "X" + number.ToString(CultureInfo.InvariantCulture));
    }
}
