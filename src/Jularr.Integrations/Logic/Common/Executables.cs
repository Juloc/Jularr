namespace Jularr.Integrations.Logic.Common
{
    // Native tools the image ships: a configured path wins, empty switches the tool off, unset finds it on the PATH.
    public static class Executables
    {
        public static string? Resolve(string? configured, params string[] names) =>
            configured is not null ? (configured.Length == 0 ? null : configured) : names.Select(Find).FirstOrDefault(path => path is not null);

        private static string? Find(string name)
        {
            var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : [""];
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(directory => extensions.Select(extension => Path.Combine(directory, name + extension)))
                .FirstOrDefault(File.Exists);
        }
    }
}
