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
                     "data-chrome-play", "data-chrome-settings-toggle", "data-chrome-fullscreen", "data-chrome-mute",
                     "data-player-previous", "data-player-next", "data-repeat-line", "data-failure-retry",
                     "data-player-action=\"seekBack10\"", "data-player-action=\"seekForward10\""
                 })
        {
            Assert.AreEqual(1, Regex.Matches(Page, Regex.Escape(hook) + "[\\s>]").Count, $"{hook} must appear once.");
        }

        // Picture-in-picture is one action in two places: the top chrome of a phone or tablet stage and the bar of a wide one.
        // The stage's layout shows one of them, never both.
        Assert.AreEqual(2, Regex.Matches(Page, "data-chrome-pip[\\s>]").Count);
        Assert.AreEqual(1, Regex.Matches(Page, "player-window-actions\"").Count);
        Assert.AreEqual(1, Regex.Matches(Page, "player-control-pip").Count);

        // The speed shortcut duplicated the speed menu entry; the top bar only carries the way back, the title and the pop-out.
        StringAssert.DoesNotMatch(Page, new Regex("data-chrome-speed"));
        StringAssert.DoesNotMatch(Page, new Regex("player-top-actions"));
        StringAssert.DoesNotMatch(Page, new Regex("data-chrome-subtitles"), "The bar's Subtitles control opens the choice; there is no second toggle.");
        StringAssert.DoesNotMatch(Page, new Regex(">\\s*✕\\s*<"), "Close buttons use the shared close icon.");
    }

    // docs/mockups/player: the bar names Subtitles, Audio, Quality and Speed with their current choice; each one, like the gear, opens
    // the one settings panel, where the choice itself is made.
    [TestMethod]
    public void TheBarNamesTheSettingsAndEveryControlOpensTheSamePanel()
    {
        foreach (var setting in new[] { "subtitles", "audio", "quality", "speed" })
        {
            Assert.AreEqual(1, Regex.Matches(Page, $"data-chrome-open-setting=\"{setting}\"").Count, setting);
            StringAssert.Contains(Page, $"data-setting-row=\"{setting}\"", $"The panel has the {setting} row the bar control opens.");
        }

        Assert.AreEqual(5, Regex.Matches(Page, "aria-controls=\"player-settings\"").Count, "Four named settings and the gear.");
        Assert.AreEqual(1, Regex.Matches(Page, "id=\"player-settings\"").Count, "There is one settings panel.");
        StringAssert.Contains(Page, "data-chrome-setting-value");
    }

    // docs/mockups/player section 3: the way back is part of the player's top chrome, in the stage and in the unavailable state alike.
    [TestMethod]
    public void TheTopChromeLeadsBackThroughOneSharedHeading()
    {
        StringAssert.Contains(Page, "<partial name=\"_VideoPlayerHeading\" model=\"heading\" />");
        var heading = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Jularr.Web", "Pages", "Library", "_VideoPlayerHeading.cshtml"));
        StringAssert.Contains(heading, "class=\"player-back\"");
        StringAssert.Contains(heading, "data-context-back");
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
