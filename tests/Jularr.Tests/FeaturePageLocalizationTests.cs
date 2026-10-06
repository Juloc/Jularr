using System.Text.RegularExpressions;

namespace Jularr.Tests;

/// <summary>
/// Guards the #185 localization migration of Books, Manga, Discover, Admin,
/// Reading, Kana, Settings, Acquisition, Appearance, Artwork, Companion,
/// LocalizationAdmin, LocalizationPreferences, Statistics, Library and Novels
/// pages (including Settings/DownloadClients and Settings/Indexers): their
/// headings and buttons must come from the UI catalog instead of being
/// hard-coded English literals. This intentionally scans only
/// headings/buttons (not every text node) because these feature pages
/// legitimately render dynamic user/library content (titles, file names,
/// provider identifiers) in many other elements.
///
/// Library, Novels (including its own _NovelChapterDrawer,
/// _NovelChapterPreparation and _NovelReaderNotes partials), Settings/DownloadClients,
/// Settings/Indexers and the shared partials they use (_AnimeAcquisitionPanel,
/// _ExternalProgress, _ExternalProgressState, _OfflineLibraryAction,
/// _ReaderSettingsPanel, _LanguageInspector) are scanned with a wider element
/// set that also covers &lt;h3&gt; and &lt;label&gt;, since those areas make
/// heavy use of both.
/// </summary>
[TestClass]
public sealed partial class FeaturePageLocalizationTests
{
    // Proper nouns, product names and technical/format identifiers that are
    // allowed to appear untranslated verbatim inside a heading, button or label.
    private static readonly string[] AllowedLiteralText =
    [
        "Jularr",
        "AniList",
        "EPUB",
        "CBZ",
        "ZIP",
        "OPDS",
        "Sonarr",
        "SABnzbd",
        "Prowlarr",
        "Newznab",
        "NAS",
        "NFO",
        "ISBN",
        "AI",
        "API",
        "URL",
        "OpenAI",
        "Codex CLI",
        "M"
    ];

    private static readonly string[] MigratedFolders =
    [
        "Books", "Manga", "Discover", "Admin", "Reading", "Kana",
        "Settings", "Acquisition", "Appearance", "Artwork", "Companion",
        "LocalizationAdmin", "LocalizationPreferences", "Statistics", "Library",
        "Novels", "Legacy"
    ];

    // Folders scanned with the wider h1/h2/h3/button/label element set because
    // they make heavy use of <h3> and <label> for genuinely fixed UI copy.
    private static readonly string[] ExtendedTagFolders =
    [
        "Library",
        "Novels",
        "Calendar",
        Path.Combine("Legacy", "Library"),
        Path.Combine("Settings", "DownloadClients"),
        Path.Combine("Settings", "Indexers"),
        Path.Combine("Legacy", "Settings", "DownloadClients"),
        Path.Combine("Legacy", "Settings", "Indexers")
    ];

    // Shared partials (outside any single feature folder) migrated alongside
    // Library for #185, also scanned with the wider element set.
    private static readonly string[] ExtendedTagSharedPartials =
    [
        "_AnimeAcquisitionPanel.cshtml",
        "_ExternalProgress.cshtml",
        "_ExternalProgressState.cshtml",
        "_MediaBannerCard.cshtml",
        "_OfflineLibraryAction.cshtml",
        "_ReaderSettingsPanel.cshtml",
        "_LanguageInspector.cshtml"
    ];

    [TestMethod]
    public void MigratedFeaturePagesHaveNoHardCodedHeadingsOrButtons()
    {
        var pagesRoot = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages");
        var files = MigratedFolders
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(pagesRoot, folder),
                "*.cshtml",
                SearchOption.AllDirectories))
            .ToArray();

        Assert.IsTrue(files.Length >= 40, "Expected page files from the migrated feature areas.");

        var findings = files
            .SelectMany(file => FindLiteralText(File.ReadAllText(file), HeadingOrButtonElement())
                .Select(text => $"{Path.GetFileName(file)}: \"{text}\""))
            .ToArray();

        Assert.AreEqual(0, findings.Length, string.Join(Environment.NewLine, findings));
    }

    [TestMethod]
    public void LibraryAndConnectionSettingsHaveNoHardCodedHeadingsButtonsOrLabels()
    {
        var pagesRoot = Path.Combine(RepositoryRoot(), "src", "Jularr.Web", "Pages");
        var sharedRoot = Path.Combine(pagesRoot, "Shared");

        var files = ExtendedTagFolders
            .SelectMany(folder => Directory.EnumerateFiles(
                Path.Combine(pagesRoot, folder),
                "*.cshtml",
                SearchOption.AllDirectories))
            .Concat(ExtendedTagSharedPartials.Select(name => Path.Combine(sharedRoot, name)))
            .ToArray();

        Assert.IsTrue(files.Length >= 10, "Expected page files from Library, the connection settings pages and their shared partials.");

        var findings = files
            .SelectMany(file => FindLiteralText(File.ReadAllText(file), ExtendedElement())
                .Select(text => $"{Path.GetFileName(file)}: \"{text}\""))
            .ToArray();

        Assert.AreEqual(0, findings.Length, string.Join(Environment.NewLine, findings));
    }

    private static IEnumerable<string> FindLiteralText(string razor, Regex elementPattern)
    {
        var markup = StripCodeBlocks(Regex.Replace(razor, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline));
        markup = Regex.Replace(markup, @"<script\b[^>]*>.*?</script>", "<script></script>", RegexOptions.Singleline);

        foreach (Match tag in elementPattern.Matches(markup))
        {
            var body = tag.Groups["body"].Value;
            var text = Regex.Replace(body, @"<[^>]+>", " ").Trim();
            text = Regex.Replace(text, @"\s+", " ");

            if (text.Length > 0
                && !text.Contains('@')
                && text.Any(char.IsLetter)
                && !AllowedLiteralText.Contains(text))
            {
                yield return text;
            }
        }
    }

    private static string StripCodeBlocks(string razor)
    {
        var result = new System.Text.StringBuilder(razor.Length);
        for (var index = 0; index < razor.Length; index++)
        {
            if (razor[index] == '@' && index + 1 < razor.Length && razor[index + 1] == '{')
            {
                var depth = 0;
                for (index++; index < razor.Length; index++)
                {
                    depth += razor[index] switch { '{' => 1, '}' => -1, _ => 0 };
                    if (depth == 0)
                    {
                        break;
                    }
                }

                continue;
            }

            result.Append(razor[index]);
        }

        return result.ToString();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }

    [GeneratedRegex(@"<(?<tag>h1|h2|button)\b[^>]*>(?<body>.*?)</\k<tag>>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadingOrButtonElement();

    [GeneratedRegex(@"<(?<tag>h1|h2|h3|button|label)\b[^>]*>(?<body>.*?)</\k<tag>>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ExtendedElement();
}
