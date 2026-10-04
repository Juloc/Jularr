using System.Text.RegularExpressions;
using Jularr.Web.Data;
using Jularr.Web.Features.Novels;
using Jularr.Web.Features.ReaderCore;
using Jularr.Web.Features.ReaderPreferences;
using Jularr.Web.Features.Speech;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

[TestClass]
public sealed class ReaderTtsTests
{
    [TestMethod]
    public async Task TtsSettingsCascadePerFieldAndPerLanguageVoice()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork();
            db.NovelWorks.Add(work);
            await db.SaveChangesAsync();

            await SaveAsync(db, ReaderPreferenceRules.UserDefaultScope, "ttsRate",
                new ReaderSettingsInput { TtsRate = 1.2 });
            await SaveAsync(db, ReaderPreferenceScopes.Type(ReaderContentType.LightNovel), "ttsPitch",
                new ReaderSettingsInput { TtsPitch = 0.9 });
            await SaveAsync(db, ReaderPreferenceScopes.Type(ReaderContentType.LightNovel), "ttsVoiceId:ja",
                new ReaderSettingsInput { TtsVoiceIds = """{"ja":"ja-type"}""" });
            await SaveAsync(db, ReaderPreferenceScopes.Genre("Fantasy", 600), "ttsVoiceId:de",
                new ReaderSettingsInput { TtsVoiceIds = """{"de":"de-genre","ja":"ignored"}""" });
            await SaveAsync(db, ReaderPreferenceScopes.Work(work.Id), "ttsVoiceId:ja",
                new ReaderSettingsInput { TtsVoiceIds = """{"ja":"ja-work"}""" });
            await SaveAsync(db, ReaderPreferenceScopes.Work(work.Id), "ttsAutoContinueChapters",
                new ReaderSettingsInput { TtsAutoContinueChapters = true });

            var settings = await GetAsync(db, work.Id);
            var workScope = ReaderPreferenceScopes.Work(work.Id);

            Assert.AreEqual("auto", settings.TtsProviderId);
            Assert.AreEqual("system", settings.EffectiveSources["ttsProviderId"]);
            Assert.AreEqual(1.2, settings.TtsRate);
            Assert.AreEqual("default", settings.EffectiveSources["ttsRate"]);
            Assert.AreEqual(0.9, settings.TtsPitch);
            Assert.AreEqual("type:light-novel", settings.EffectiveSources["ttsPitch"]);
            Assert.AreEqual(1, settings.TtsVolume);
            Assert.AreEqual("ja-work", settings.TtsVoiceIds["ja"]);
            Assert.AreEqual(workScope, settings.EffectiveSources["ttsVoiceId:ja"]);
            Assert.AreEqual("de-genre", settings.TtsVoiceIds["de"]);
            StringAssert.StartsWith(settings.EffectiveSources["ttsVoiceId:de"], "genre:600:fantasy");
            Assert.IsTrue(settings.TtsAutoContinueChapters);
            Assert.IsTrue(settings.HasWorkOverride);

            await ReaderPreferenceStore.ResetScopeFieldAsync(
                db, "profile", workScope, "ttsVoiceId:ja", CancellationToken.None);
            await ReaderPreferenceStore.ResetScopeFieldAsync(
                db, "profile", workScope, "ttsAutoContinueChapters", CancellationToken.None);

            var inherited = await GetAsync(db, work.Id);

            Assert.AreEqual("ja-type", inherited.TtsVoiceIds["ja"]);
            Assert.AreEqual("type:light-novel", inherited.EffectiveSources["ttsVoiceId:ja"]);
            Assert.AreEqual("de-genre", inherited.TtsVoiceIds["de"]);
            Assert.IsFalse(inherited.TtsAutoContinueChapters);
            Assert.IsFalse(inherited.HasWorkOverride, "The emptied work row must be removed.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task TtsValuesAreNormalizedAndUnknownVoiceKeysAreRejected()
    {
        var path = TempDatabasePath();

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork();
            db.NovelWorks.Add(work);
            await db.SaveChangesAsync();

            await SaveAsync(db, ReaderPreferenceRules.UserDefaultScope, "ttsRate",
                new ReaderSettingsInput { TtsRate = 9 });
            await SaveAsync(db, ReaderPreferenceRules.UserDefaultScope, "ttsVolume",
                new ReaderSettingsInput { TtsVolume = -3 });
            await SaveAsync(db, ReaderPreferenceRules.UserDefaultScope, "ttsProviderId",
                new ReaderSettingsInput { TtsProviderId = "remote-cloud" });

            var settings = await GetAsync(db, work.Id);

            Assert.AreEqual(2.5, settings.TtsRate);
            Assert.AreEqual(0, settings.TtsVolume);
            Assert.AreEqual("auto", settings.TtsProviderId);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => SaveAsync(
                db,
                ReaderPreferenceRules.UserDefaultScope,
                "ttsVoiceId:",
                new ReaderSettingsInput { TtsVoiceIds = """{"":"x"}""" }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void SystemPresetsNeverAutoContinueAcrossChapters()
    {
        foreach (var type in Enum.GetValues<ReaderContentType>())
        {
            var preset = ReaderPresetCatalog.For(type);
            Assert.IsFalse(preset.TtsAutoContinueChapters, type.ToString());
            Assert.AreEqual("auto", preset.TtsProviderId);
        }
    }

    [TestMethod]
    public async Task EffectiveSettingsFeedTheSpeechResolverWithUsefulReasons()
    {
        var path = TempDatabasePath();
        ReaderSettingsSnapshot settings;

        try
        {
            await using var db = await CreateDatabaseAsync(path);
            var work = NewWork();
            db.NovelWorks.Add(work);
            await db.SaveChangesAsync();

            await ReaderPreferenceStore.SaveUserDefaultsAsync(
                db,
                "profile",
                new ReaderSettingsInput
                {
                    TtsProviderId = "device",
                    TtsVoiceIds = """{"de":"de-voice","ja-JP":"ja-voice"}""",
                    TtsRate = 1.3
                },
                CancellationToken.None);

            settings = await GetAsync(db, work.Id);
        }
        finally
        {
            File.Delete(path);
        }

        var austrian = settings.SpeechPreferencesFor("de_at");
        Assert.AreEqual("de-voice", austrian.VoiceId, "Base-language voice applies to regional text.");
        Assert.AreEqual("de-AT", austrian.Language);
        Assert.AreEqual("device", austrian.ProviderId);
        Assert.AreEqual(1.3, austrian.Rate);
        Assert.IsNull(settings.SpeechPreferencesFor("ko").VoiceId);
        Assert.IsNull((settings with { TtsProviderId = "auto" }).SpeechPreferencesFor("de").ProviderId);

        SpeechProviderDescriptor device = new(
            "device", "Gerät", SpeechProviderKind.Device, true,
            SpeechProviderCapabilities.PlatformDefaultVoice);
        SpeechVoiceDescriptor[] voices =
        [
            new("device", "de-voice", "Deutsch", "de-DE"),
            new("device", "ja-other", "Japanisch", "ja-JP")
        ];

        var selected = SpeechAvailabilityResolver.Explain(austrian, [device], voices);
        Assert.IsTrue(selected.IsAvailable);
        Assert.AreEqual("de-voice", selected.Resolution!.VoiceId);

        var automatic = SpeechAvailabilityResolver.Explain(
            (settings with { TtsProviderId = "auto" }).SpeechPreferencesFor("de-DE"),
            [device],
            [new("device", "de-default", "Standard", "de-DE", IsDefault: true), .. voices]);
        Assert.AreEqual("de-voice", automatic.Resolution!.VoiceId,
            "A chosen voice wins even when the provider is automatic.");
        Assert.AreEqual("selected-voice", automatic.Resolution.Reason);

        var missingVoice = SpeechAvailabilityResolver.Explain(
            settings.SpeechPreferencesFor("ja"), [device], voices);
        Assert.AreEqual("ja-other", missingVoice.Resolution!.VoiceId,
            "A voice missing on this device falls back to a compatible one.");

        Assert.AreEqual(
            SpeechAvailability.NoProvider,
            SpeechAvailabilityResolver.Explain(austrian, [], voices).UnavailableReason);
        Assert.AreEqual(
            SpeechAvailability.ProviderUnavailable,
            SpeechAvailabilityResolver.Explain(
                austrian, [device with { IsAvailable = false }], voices).UnavailableReason);
        Assert.AreEqual(
            SpeechAvailability.NoVoiceForLanguage,
            SpeechAvailabilityResolver.Explain(
                settings.SpeechPreferencesFor("ko"),
                [device with { Capabilities = SpeechProviderCapabilities.None }],
                voices).UnavailableReason);
    }

    [TestMethod]
    public void VoiceMapIsBoundedAndIgnoresInvalidEntries()
    {
        var json = "{" + string.Join(",", Enumerable.Range(0, 40)
            .Select(i => $"\"x{i:00}\":\"voice-{i}\"")) + ",\"\":\"bad\",\"de\":\"auto\"}";

        var map = SpeechVoiceMap.Parse(json);

        Assert.AreEqual(SpeechVoiceMap.MaxEntries, map.Count);
        Assert.IsFalse(map.ContainsKey("de"), "\"auto\" means inherit, not a voice id.");
        Assert.AreEqual(0, SpeechVoiceMap.Parse("not json").Count);
    }

    [TestMethod]
    public void TtsCapabilityIsOnlyForReflowableText()
    {
        foreach (var type in new[]
                 {
                     ReaderContentType.Book, ReaderContentType.LightNovel, ReaderContentType.WebNovel
                 })
        {
            Assert.IsTrue(ReaderDocumentDescriptor.Create(Guid.NewGuid(), type, "t")
                .Capabilities.SupportsTts);
        }

        Assert.IsFalse(ReaderDocumentDescriptor.Create(
            Guid.NewGuid(), ReaderContentType.Manga, "t").Capabilities.SupportsTts);
        Assert.IsFalse(ReaderDocumentDescriptor.Create(
            Guid.NewGuid(), ReaderContentType.FixedDocument, "t").Capabilities.SupportsTts);

        var document = ReaderDocumentDescriptor.Create(
            Guid.NewGuid(),
            ReaderContentType.Book,
            "t",
            languages: [new("en_us", "Englisch"), new("en-US", "Duplicate"), new("", "none")]);
        Assert.AreEqual(1, document.Languages.Count);
        Assert.AreEqual("en-US", document.Languages[0].Tag);
    }

    [TestMethod]
    public void SharedShellMarkupRendersTtsControlsOnlyBehindTheCapability()
    {
        var root = RepositoryRoot();
        var shared = Read(root, "src", "Jularr.Web", "Pages", "Shared", "_ReaderSettingsPanel.cshtml");

        var (gated, ungated) = SplitCapabilityBlocks(shared, "@if (capabilities.SupportsTts)");

        Assert.AreEqual(3, gated.Length, "Toggle, settings section and player bar are gated.");
        Assert.IsTrue(gated.Any(body => body.Contains("data-reader-tts-toggle", StringComparison.Ordinal)));
        Assert.IsTrue(gated.Any(body => body.Contains("data-reader-tts-settings", StringComparison.Ordinal)));
        Assert.IsTrue(gated.Any(body => body.Contains("data-reader-tts-bar", StringComparison.Ordinal)));
        Assert.IsFalse(ungated.Contains("data-reader-tts", StringComparison.Ordinal));

        foreach (var page in new[] { "Books", "Novels" })
        {
            var markup = Read(root, "src", "Jularr.Web", "Pages", page, "Read.cshtml");
            StringAssert.Contains(markup, "~/js/tts.js");
            StringAssert.Contains(markup, "~/js/reader-tts.js");
            StringAssert.Contains(markup, "data-reader-next-chapter");
        }

        var shell = Read(root, "src", "Jularr.Web", "wwwroot", "js", "reader-shell.js");
        StringAssert.Contains(shell, "window.JularrReaderTts?.mount(api)");
        StringAssert.Contains(shell, "[\"tts\", \"Vorlesen\"]");
    }

    [TestMethod]
    public void ReaderTtsNeverWritesProgressOrSendsOrStoresText()
    {
        var script = Read(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "reader-tts.js");

        foreach (var forbidden in new[]
                 {
                     "fetch(", "XMLHttpRequest", "sendBeacon", "localStorage", "indexedDB",
                     "data-progress-form", "data-book-progress-form", "positionPermille",
                     "anchorParagraphIndex"
                 })
        {
            Assert.IsFalse(
                script.Contains(forbidden, StringComparison.Ordinal),
                $"reader-tts.js must not use {forbidden}.");
        }

        var stored = Regex.Matches(script, @"sessionStorage\.setItem\((?<args>.*?)\)\);", RegexOptions.Singleline);
        Assert.AreEqual(1, stored.Count);
        StringAssert.Contains(stored[0].Groups["args"].Value, "chapterId");
        Assert.IsFalse(stored[0].Groups["args"].Value.Contains("text", StringComparison.OrdinalIgnoreCase));
    }

    // The Reader must serialise device utterances because Safari can abort a prefilled Web Speech queue.
    [TestMethod]
    public void ReaderTtsDoesNotPrefillTheBrowserSpeechQueue()
    {
        var script = Read(RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "reader-tts.js");

        StringAssert.Contains(script, "const LOOK_AHEAD = 0;");
    }

    private static (string[] Gated, string Ungated) SplitCapabilityBlocks(
        string markup,
        string condition)
    {
        var gated = new List<string>();
        var ungated = new System.Text.StringBuilder();
        var cursor = 0;

        while (true)
        {
            var start = markup.IndexOf(condition, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                ungated.Append(markup, cursor, markup.Length - cursor);
                break;
            }

            ungated.Append(markup, cursor, start - cursor);
            var open = markup.IndexOf('{', start);
            var depth = 0;
            var end = open;
            for (; end < markup.Length; end++)
            {
                if (markup[end] == '{') depth++;
                else if (markup[end] == '}' && --depth == 0) break;
            }

            gated.Add(markup[(open + 1)..end]);
            cursor = end + 1;
        }

        return (gated.ToArray(), ungated.ToString());
    }

    private static Task SaveAsync(
        AppDbContext db,
        string scope,
        string key,
        ReaderSettingsInput input) =>
        ReaderPreferenceStore.SaveScopeFieldAsync(db, "profile", scope, key, input, CancellationToken.None);

    private static Task<ReaderSettingsSnapshot> GetAsync(AppDbContext db, Guid workId) =>
        ReaderPreferenceStore.GetAsync(
            db,
            "profile",
            workId,
            """["Fantasy"]""",
            ReaderContentType.LightNovel,
            CancellationToken.None);

    private static NovelWork NewWork() =>
        new()
        {
            SourceProvider = "test",
            SourceKey = Guid.NewGuid().ToString("N"),
            SourceUrl = "https://example.invalid/work",
            Title = "Reader TTS test"
        };

    private static string TempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"jularr-reader-tts-{Guid.NewGuid():N}.db");

    private static async Task<AppDbContext> CreateDatabaseAsync(string path)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;
        var db = new AppDbContext(options);
        await DatabaseMigrationBridge.UpgradeAsync(db);
        return db;
    }

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine([root, .. parts]));

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
}
