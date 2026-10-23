namespace Jularr.Backend.Data.Enums.Library
{
    // The languages an administrator can give a file's audio; tracks keep the three-letter codes a probe reads.
    public static class FileLanguages
    {
        public static IReadOnlyList<(string Two, string Three)> Known { get; } =
        [
            ("en", "eng"), ("de", "ger"), ("fr", "fre"), ("es", "spa"), ("it", "ita"), ("nl", "dut"), ("pl", "pol"), ("pt", "por"), ("ru", "rus"), ("ja", "jpn"), ("ko", "kor"), ("zh", "chi")
        ];
    }
}
