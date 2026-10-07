using Jularr.Web.Features.Instance;
using Microsoft.AspNetCore.Http;

namespace Jularr.Tests;

/// <summary>A preset only sets the one canonical module store; the Media Manager preset leaves a server that plans nothing for playback.</summary>
[TestClass]
public sealed class InstanceModulePresetTests
{
    [TestMethod]
    public void TheMediaManagerPresetTurnsPlaybackLearningAndTrackingOffAcquisitionOnAndKeepsTheChosenMediaTypes()
    {
        var chosen = InstanceModuleSettings.Default.With(InstanceModule.Anime, false).With(InstanceModule.Acquisition, false);

        var manager = InstanceModulePresets.Apply(InstancePreset.MediaManager, chosen);

        Assert.IsTrue(manager.IsEnabled(InstanceModule.Acquisition));
        Assert.IsFalse(manager.IsEnabled(InstanceModule.Playback));
        Assert.IsFalse(manager.IsEnabled(InstanceModule.Learning));
        Assert.IsFalse(manager.IsEnabled(InstanceModule.Tracking));
        Assert.IsFalse(manager.IsEnabled(InstanceModule.Anime), "The media types stay as the owner chose them.");
        Assert.IsTrue(manager.IsEnabled(InstanceModule.Movie));
        Assert.AreEqual(InstancePreset.MediaManager, InstanceModulePresets.Detect(manager));
    }

    [TestMethod]
    public void FullTurnsEverythingOnAndAnyOtherCombinationIsCustom()
    {
        var manager = InstanceModulePresets.Apply(InstancePreset.MediaManager, InstanceModuleSettings.Default);

        Assert.AreEqual(InstancePreset.Full, InstanceModulePresets.Detect(InstanceModulePresets.Apply(InstancePreset.Full, manager)));
        Assert.AreEqual(InstancePreset.Full, InstanceModulePresets.Detect(InstanceModuleSettings.Default));
        Assert.AreEqual(InstancePreset.Custom, InstanceModulePresets.Detect(InstanceModuleSettings.Default.With(InstanceModule.Playback, false)));
        Assert.AreEqual(InstancePreset.Custom, InstanceModulePresets.Detect(manager.With(InstanceModule.Tracking, true)));
        Assert.AreEqual(InstancePreset.Custom, InstanceModulePresets.Detect(manager.With(InstanceModule.Movie, false).With(InstanceModule.Tv, false).With(InstanceModule.Anime, false).With(InstanceModule.Manga, false).With(InstanceModule.Novel, false).With(InstanceModule.Book, false).With(InstanceModule.Audiobook, false).With(InstanceModule.Music, false)), "A manager that serves no media type is not a Media Manager.");
        Assert.AreEqual(manager, InstanceModulePresets.Apply(InstancePreset.Custom, manager));
    }

    [TestMethod]
    public void UnderTheMediaManagerPresetEveryPlayerRouteLearningRouteAndTrackingRouteIsGatedWhileManagementRoutesAreNot()
    {
        var manager = InstanceModulePresets.Apply(InstancePreset.MediaManager, InstanceModuleSettings.Default);
        bool Reachable(string path) => InstanceModuleRoutes.Resolve(new PathString(path)).All(manager.IsEnabled);

        foreach (var gated in new[] { "/Library/Watch/abc", "/api/client/v1/video/playback-plan", "/api/client/v1/stream-sessions", "/api/client/v1/episodes/1/hls", "/Learn", "/Kana", "/Settings/AniList" })
        {
            Assert.IsFalse(Reachable(gated), $"{gated} must be gated when the module is off.");
        }

        foreach (var open in new[] { "/Admin/Requests", "/Admin/Wanted", "/Admin/AcquisitionProfiles", "/Admin/ReadingManualSearch/abc", "/Admin/Music", "/Music/Album/abc", "/Admin/Usenet", "/Requests" })
        {
            Assert.IsTrue(Reachable(open), $"{open} is management and stays reachable.");
        }
    }
}
