using System.Text.RegularExpressions;

namespace Jularr.Tests;

[TestClass]
public sealed class WatchPageMarkupTests
{
    private static readonly string Page = EpisodePlayerSource.Read(FindRepositoryRoot());

    [TestMethod]
    public void PlayerHasOwnChromeWithExactlyOneTimelineAndNoNativeControls()
    {
        var video = Regex.Match(Page, "<video[^>]*>").Value;
        Assert.IsFalse(video.Contains("controls", StringComparison.Ordinal), video);

        Assert.AreEqual(1, Regex.Matches(Page, "data-playback-timeline[\\s/>]").Count, "Exactly one timeline.");
        Assert.AreEqual(1, Regex.Matches(Page, "type=\"range\"[^>]*data-playback-timeline").Count
            + Regex.Matches(Page, "data-playback-timeline[^-][^>]*type=\"range\"").Count);
    }

    [TestMethod]
    public void EveryPlayerControlExistsExactlyOnce()
    {
        foreach (var hook in new[]
                 {
                     "data-chrome-play", "data-chrome-settings-toggle", "data-chrome-fullscreen", "data-chrome-subtitles",
                     "data-chrome-pip", "data-chrome-mute", "data-player-previous", "data-player-next", "data-repeat-line",
                     "data-player-action=\"seekBack10\"", "data-player-action=\"seekForward10\""
                 })
        {
            Assert.AreEqual(1, Regex.Matches(Page, Regex.Escape(hook) + "[\\s>]").Count, $"{hook} must appear once.");
        }

        // The speed shortcut duplicated the speed menu entry; the top bar only carries the title.
        StringAssert.DoesNotMatch(Page, new Regex("data-chrome-speed"));
        StringAssert.DoesNotMatch(Page, new Regex("player-top-actions"));
        StringAssert.DoesNotMatch(Page, new Regex(">\\s*✕\\s*<"), "Close buttons use the shared close icon.");
    }

    // docs/mockups/player: Previous and Next are part of the centred transport group, never of the volume/settings bar.
    [TestMethod]
    public void PreviousAndNextEpisodeBelongToTheTransportGroup()
    {
        var transportStart = Page.IndexOf("class=\"player-center\"", StringComparison.Ordinal);
        var barStart = Page.IndexOf("class=\"player-bottom\"", StringComparison.Ordinal);
        Assert.IsTrue(transportStart > 0 && barStart > transportStart);
        var transport = Page[transportStart..barStart];

        StringAssert.Contains(transport, "data-player-previous");
        StringAssert.Contains(transport, "data-player-next");
        StringAssert.DoesNotMatch(Page[barStart..], new Regex("data-player-(previous|next)"));
    }

    [TestMethod]
    public void PictureSubtitlesAreMarkedAsBurnedInInTheMenu()
    {
        StringAssert.Contains(Page, "ui[\"library.episode.subtitleBurnedIn\"]");
        StringAssert.Contains(Page, "ui[\"library.episode.subtitleUnsupported\"]");
        StringAssert.Contains(Page, "data-image=");
        StringAssert.Contains(Page, "data-subtitle-hint");
    }

    [TestMethod]
    public void EverySettingLivesInsideThePlayerStage()
    {
        var stageStart = Page.IndexOf("data-video-stage", StringComparison.Ordinal);
        var stageEnd = Page.IndexOf("data-player-controls-data", StringComparison.Ordinal);
        Assert.IsTrue(stageStart > 0 && stageEnd > stageStart);
        var stage = Page[stageStart..stageEnd];

        foreach (var hook in new[]
                 {
                     "data-playback-speed", "data-subtitle-track", "data-audio-track", "data-quality-cap",
                     "data-playback-mode", "data-autoplay-toggle", "data-restart", "data-save-playback-defaults",
                     "data-chrome-fullscreen", "data-chrome-pip", "data-chrome-volume"
                 })
        {
            StringAssert.Contains(stage, hook, $"{hook} must be part of the player.");
        }
    }

    // Owner-only mapping/source management (subtitle sources, AniList mapping) moved into the
    // shared _ManageSheet partial (#519, part of epic #510); the sheet itself is only rendered
    // inside an @if (Model.IsOwner) block, same guarantee as the previous bespoke dialog.
    [TestMethod]
    public void SourceAndMappingManagementIsOwnerOnly()
    {
        var ownerBlock = Page.IndexOf("@if (Model.IsOwner)", StringComparison.Ordinal);
        Assert.IsTrue(ownerBlock > 0, "Owner-only manage sheet block exists.");

        var subtitleSources = Page.IndexOf("_EpisodeSubtitleSources", StringComparison.Ordinal);
        Assert.IsTrue(subtitleSources > ownerBlock, "Subtitle source mapping renders only in the owner-only block.");

        var manageSheet = Page.IndexOf("<partial name=\"_ManageSheet\"", StringComparison.Ordinal);
        Assert.IsTrue(manageSheet > subtitleSources, "Subtitle source mapping renders inside the owner-only Manage sheet.");

        Assert.AreEqual(1, Regex.Matches(Page, "CanManageMapping: true").Count);
        StringAssert.Contains(Page, "Model.ExternalProgress is { IsMatched: true, ReviewReason: null } externalProgress");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
