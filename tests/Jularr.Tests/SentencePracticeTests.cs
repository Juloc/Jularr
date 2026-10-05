using Jularr.Web.Features.Ai;
using Jularr.Web.Features.Learning;
using Jularr.Web.Features.Learning.LanguageAssistance;
using Jularr.Web.Features.Learning.Sentences;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Subtitles;
using Jularr.Web.Features.Vocabulary;
using Microsoft.EntityFrameworkCore;

namespace Jularr.Tests;

/// <summary>
/// #233: sentence practice is its own module. It prefers sentences from content
/// the profile consumed and words it Saved or is Learning, supports cloze,
/// comprehension and anime listening modes and only reads cached explanations.
/// </summary>
[TestClass]
public sealed class SentencePracticeTests
{
    [TestMethod]
    public void SuitabilityFollowsTheContentLanguage()
    {
        Assert.IsTrue(SentencePracticeText.IsSuitable("今日は学校に行く。", "ja"));
        Assert.IsFalse(SentencePracticeText.IsSuitable("hello world", "ja"));
        Assert.IsFalse(SentencePracticeText.IsSuitable("あ\nい", "ja"));
        Assert.IsTrue(SentencePracticeText.IsSuitable("The cat sleeps.", "en"));
        Assert.IsFalse(SentencePracticeText.IsSuitable("123", "en"));
        Assert.IsFalse(SentencePracticeText.IsSuitable(new string('a', 201), "en"));
    }

    [TestMethod]
    public void ClozeBlanksTheStudiedWord()
    {
        var tokens = new[]
        {
            new LanguageTextToken("The", "The", null, null, null, true),
            new LanguageTextToken(" ", null, null, null, null, false),
            new LanguageTextToken("Cat", "Cat", null, null, null, true),
            new LanguageTextToken(".", null, null, null, null, false)
        };

        Assert.AreEqual("The ____.", SentencePracticeService.BuildCloze(tokens, "cat", "en"));
        Assert.IsNull(SentencePracticeService.BuildCloze(tokens, "dog", "en"));
        Assert.IsNull(SentencePracticeService.BuildCloze(tokens, null, "en"));
    }

    [TestMethod]
    public async Task PrefersRecordedContextsThenWatchedEpisodesAndActiveWords()
    {
        await using var fixture = await LanguageInspectorFixture.CreateAsync();
        await fixture.SetProfileModeAsync(LearningMode.Study);

        // A word saved in the book reader records the book sentence as context.
        await fixture.Inspector().SetStateAsync(
            new LanguageWordStateRequest("猫", "saved", fixture.BookContext(3, "本の猫。")),
            CancellationToken.None);

        var unwatched = await AddEpisodeCueAsync(fixture, "unwatched", "猫がいる。", 5_000, watched: false);
        await fixture.SeedTermStateAsync("犬", UserTermState.Known);
        var watched = await AddEpisodeCueAsync(fixture, "watched", "犬がいる。", 6_000, watched: true);
        await LinkTermAsync(fixture, "猫", unwatched, 5_000, occurrences: 9);
        await LinkTermAsync(fixture, "犬", watched, 6_000, occurrences: 1);

        var comprehension = await Service(fixture).LoadAsync(
            LanguageInspectorFixture.Profile,
            SentencePracticeMode.Comprehension,
            10,
            withCachedExplanations: false,
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "本の猫。", "猫がいる。", "犬がいる。" },
            comprehension.Select(x => x.Text).ToArray(),
            "Recorded contexts first, then Saved/Learning words, then Known words.");
        var book = comprehension[0];
        Assert.AreEqual(LanguageSourceType.Book, book.Source.Type);
        Assert.AreEqual(3, book.Source.Position.Paragraph);
        Assert.AreEqual($"/Books/Read/{fixture.ChapterId}", book.Source.Url);
        Assert.AreEqual("Inspector Book", book.Source.Title);
        Assert.AreEqual("猫", book.TargetCanonical);
        Assert.AreEqual("Katze", book.TargetMeaning);
        Assert.AreEqual(UserTermState.Saved, book.TargetState);

        var listening = await Service(fixture).LoadAsync(
            LanguageInspectorFixture.Profile,
            SentencePracticeMode.Listening,
            10,
            withCachedExplanations: false,
            CancellationToken.None);
        Assert.IsTrue(listening.All(x => x.Source.Type == LanguageSourceType.Anime));
        CollectionAssert.AreEqual(
            new[] { "猫がいる。", "犬がいる。" },
            listening.Select(x => x.Text).ToArray());
        Assert.AreEqual($"/Library/Episode/{unwatched}?at=5000", listening[0].Source.Url);

        var cloze = await Service(fixture).LoadAsync(
            LanguageInspectorFixture.Profile,
            SentencePracticeMode.Cloze,
            10,
            withCachedExplanations: false,
            CancellationToken.None);
        Assert.AreEqual("本の＿＿。", cloze[0].Cloze);
        Assert.IsTrue(cloze.All(x => x.Cloze is not null));
    }

    [TestMethod]
    public async Task WatchedEpisodesComeFirstForEquallyActiveWords()
    {
        await using var fixture = await LanguageInspectorFixture.CreateAsync();
        await fixture.SetProfileModeAsync(LearningMode.Study);
        await fixture.SeedTermStateAsync("猫", UserTermState.Learning);
        await fixture.SeedTermStateAsync("犬", UserTermState.Saved);
        var unwatched = await AddEpisodeCueAsync(fixture, "unwatched", "猫がいる。", 1_000, watched: false);
        var watched = await AddEpisodeCueAsync(fixture, "watched", "犬がいる。", 2_000, watched: true);
        await LinkTermAsync(fixture, "猫", unwatched, 1_000, occurrences: 20);
        await LinkTermAsync(fixture, "犬", watched, 2_000, occurrences: 1);

        var items = await Service(fixture).LoadAsync(
            LanguageInspectorFixture.Profile,
            SentencePracticeMode.Comprehension,
            10,
            withCachedExplanations: false,
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "犬がいる。", "猫がいる。" },
            items.Select(x => x.Text).ToArray());
    }

    [TestMethod]
    public async Task CachedExplanationsAreAttachedWithoutCallingTheProvider()
    {
        await using var fixture = await LanguageInspectorFixture.CreateAsync();
        await fixture.SetProfileModeAsync(LearningMode.Study);
        await fixture.Inspector().SetStateAsync(
            new LanguageWordStateRequest("猫", "saved", fixture.BookContext(3, "本の猫。")),
            CancellationToken.None);

        var withoutCache = await Service(fixture).LoadAsync(
            LanguageInspectorFixture.Profile,
            SentencePracticeMode.Comprehension,
            5,
            withCachedExplanations: true,
            CancellationToken.None);
        Assert.IsNull(withoutCache.Single().Explanation);
        Assert.AreEqual(0, fixture.Explainer.Calls);

        await new AiSentenceExplanationService(fixture.Db, fixture.Explainer)
            .ExplainAsync("本の猫。", CancellationToken.None);
        Assert.AreEqual(1, fixture.Explainer.Calls);

        var cached = await Service(fixture).LoadAsync(
            LanguageInspectorFixture.Profile,
            SentencePracticeMode.Comprehension,
            5,
            withCachedExplanations: true,
            CancellationToken.None);
        Assert.AreEqual("There is a cat.", cached.Single().Explanation?.Translation);
        Assert.IsTrue(cached.Single().Explanation?.FromCache);
        Assert.AreEqual(1, fixture.Explainer.Calls);
    }

    [TestMethod]
    public void ModesParseWithClozeAsDefault()
    {
        Assert.AreEqual(SentencePracticeMode.Listening, SentencePracticeModes.Parse("listening"));
        Assert.AreEqual(SentencePracticeMode.Comprehension, SentencePracticeModes.Parse("Comprehension"));
        Assert.AreEqual(SentencePracticeMode.Cloze, SentencePracticeModes.Parse(null));
        Assert.AreEqual(SentencePracticeMode.Cloze, SentencePracticeModes.Parse("3"));
        Assert.AreEqual(SentencePracticeMode.Cloze, SentencePracticeModes.Parse("karaoke"));
        Assert.AreEqual("comprehension", SentencePracticeModes.Key(SentencePracticeMode.Comprehension));
    }

    private static SentencePracticeService Service(LanguageInspectorFixture fixture) =>
        new(fixture.Db, fixture.Analyzer, new AiSentenceExplanationService(fixture.Db, fixture.Explainer));

    private static async Task<Guid> AddEpisodeCueAsync(
        LanguageInspectorFixture fixture,
        string key,
        string text,
        int startMs,
        bool watched)
    {
        var anime = new Anime { Key = key, Title = key };
        var episode = new Episode
        {
            AnimeId = anime.Id,
            SeasonNumber = 1,
            Number = 1,
            Title = key,
            DiscoveredAt = DateTime.UtcNow
        };
        var track = new SubtitleTrack
        {
            EpisodeId = episode.Id,
            Path = $"/tmp/{key}.ja.srt",
            Language = "ja",
            Format = "srt",
            SourceUpdatedAt = DateTime.UtcNow
        };
        fixture.Db.AddRange(anime, episode, track);
        fixture.Db.SubtitleCues.Add(new SubtitleCue
        {
            SubtitleTrackId = track.Id,
            StartMs = startMs,
            EndMs = startMs + 1_000,
            Text = text
        });

        await fixture.Db.SaveChangesAsync();
        if (watched)
        {
            await CanonicalProgressSeed.SetAsync(fixture.Db, LanguageInspectorFixture.Profile, episode.Id, startMs, null, false);
        }

        return episode.Id;
    }

    private static async Task LinkTermAsync(
        LanguageInspectorFixture fixture,
        string canonical,
        Guid episodeId,
        int startMs,
        int occurrences)
    {
        var term = await fixture.Db.Terms.SingleOrDefaultAsync(x => x.Language == "ja" && x.Canonical == canonical);
        if (term is null)
        {
            term = new Term { Language = "ja", Canonical = canonical };
            fixture.Db.Terms.Add(term);
        }

        fixture.Db.EpisodeTerms.Add(new EpisodeTerm
        {
            EpisodeId = episodeId,
            TermId = term.Id,
            Occurrences = occurrences,
            FirstCueStartMs = startMs
        });
        await fixture.Db.SaveChangesAsync();
    }
}
