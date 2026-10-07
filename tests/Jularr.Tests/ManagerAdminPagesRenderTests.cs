using System.Net;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Music;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jularr.Tests;

/// <summary>The pages of the manager run rendered and posted end to end: the profile editor, the module presets, Manga and Light Novel Manual Search and the Music library.</summary>
[TestClass]
public sealed class ManagerAdminPagesRenderTests
{
    private const string DuneRelease = "Dune.2021.1080p.WEB-DL.x264-GROUP";

    [TestMethod]
    public async Task TheProfileEditorListsEditsAndExplainsInvalidInputWithoutStoringIt()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await using var host = await VideoAdminPageHost.CreateAsync(video);

        var html = await host.GetHtmlAsync("/Admin/AcquisitionProfiles?id=movie-1080p");
        StringAssert.Contains(html, "Movies 1080p");
        StringAssert.Contains(html, "name=\"QualityOrder\"");
        StringAssert.Contains(html, "BLURAY-1080p");
        StringAssert.Contains(html, "Default for Movies");

        var store = video.Get<QualityProfileStore>();
        var profile = await store.ResolveAsync(MediaAcquisitionKind.Movie, null);
        var form = QualityProfileEditing.ToForm(profile);
        form.UpgradeCutoffQuality = "BLURAY-1080p";
        var saved = await host.PostAsync("/Admin/AcquisitionProfiles", $"/Admin/AcquisitionProfiles?handler=Save", FormFields(form));
        Assert.AreEqual(HttpStatusCode.Redirect, saved);
        Assert.AreEqual("BLURAY-1080p", (await store.ResolveAsync(MediaAcquisitionKind.Movie, null)).UpgradeCutoffQuality, "The saved cutoff is what the engine resolves next.");

        form.Rules.Add(new ScoreRuleRow { Effect = "Reject", Field = "ReleaseGroup", Match = "Equals", Value = "BAD" });
        form.Tiers.Add(new FallbackTierRow { Minutes = "120", Qualities = [form.QualityOrder[^1]] });
        Assert.AreEqual(HttpStatusCode.Redirect, await host.PostAsync("/Admin/AcquisitionProfiles", "/Admin/AcquisitionProfiles?handler=Save", FormFields(form)));
        var stored = await store.ResolveAsync(MediaAcquisitionKind.Movie, null);
        Assert.AreEqual(ReleaseRuleEffect.Reject, stored.ScoreRules.Single(rule => rule.Value == "BAD").EffectiveEffect, "A rule row the owner added is stored with its effect.");
        Assert.AreEqual(120, stored.FallbackTiers.Single().AfterMinutes);

        form.Rules = [new ScoreRuleRow { Effect = "Prefer", Field = "NoSuchField", Match = "Equals", Value = "x", Score = "5", Name = "broken" }];
        var rejected = await host.PostAsync("/Admin/AcquisitionProfiles", "/Admin/AcquisitionProfiles?handler=Save", FormFields(form));
        Assert.AreEqual(HttpStatusCode.OK, rejected, "An invalid rule is explained on the page, not stored.");
        Assert.AreEqual(0, (await store.ResolveAsync(MediaAcquisitionKind.Movie, null)).ScoreRules.Count(rule => rule.Name == "Rule 1"));
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync("/Admin/AcquisitionProfiles?id=no-such-profile"));
    }

    [TestMethod]
    public async Task TheInstancePageAppliesThePresetsToTheOneModuleStore()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await using var host = await VideoAdminPageHost.CreateAsync(video);

        var html = await host.GetHtmlAsync("/Admin/Instance");
        StringAssert.Contains(html, "Media Manager");
        StringAssert.Contains(html, "admin-tag admin-tag-accent\">Current</span>", "The preset the switches match is marked on its card.");

        Assert.AreEqual(HttpStatusCode.Redirect, await host.PostAsync("/Admin/Instance", "/Admin/Instance?handler=Preset", [new("preset", "MediaManager")]));
        var manager = await host.Modules.GetAsync();
        Assert.IsFalse(manager.IsEnabled(InstanceModule.Playback));
        Assert.IsTrue(manager.IsEnabled(InstanceModule.Acquisition));
        StringAssert.Contains(await host.GetHtmlAsync("/Admin/Instance"), "admin-tag admin-tag-accent\">Current</span>");

        Assert.AreEqual(HttpStatusCode.BadRequest, await host.PostAsync("/Admin/Instance", "/Admin/Instance?handler=Preset", [new("preset", "Custom")]));
        Assert.AreEqual(HttpStatusCode.Redirect, await host.PostAsync("/Admin/Instance", "/Admin/Instance?handler=Preset", [new("preset", "Full")]));
        Assert.IsTrue((await host.Modules.GetAsync()).IsEnabled(InstanceModule.Playback));
    }

    [TestMethod]
    public async Task MangaManualSearchRendersTheRequestAndExplainsEveryReleaseItFound()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await using var host = await VideoAdminPageHost.CreateAsync(video);
        var requests = video.Get<AcquisitionAccessStore>();
        var request = await requests.CreateAsync(new AcquisitionRequestDraft(MediaAcquisitionKind.Manga, "anilist", "154587", "Frieren", null, null), "owner", AcquisitionRequestStatus.Approved, "owner", CancellationToken.None);

        var page = await host.GetHtmlAsync($"/Admin/ReadingManualSearch/{request.Id:D}");
        StringAssert.Contains(page, "Frieren");
        StringAssert.Contains(page, "Start search");

        var results = await host.GetHtmlAsync($"/Admin/ReadingManualSearch/{request.Id:D}?search=normal&refresh=true");
        StringAssert.Contains(results, "Release candidates");
        StringAssert.Contains(results, "Dune", "Every release the indexers returned is listed, accepted or not.");
        StringAssert.Contains(results, "Another title or unit", "A release for another title says why it is rejected.");
        Assert.IsFalse(results.Contains("Select and download", StringComparison.Ordinal), "A rejected release can never be grabbed.");
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync($"/Admin/ReadingManualSearch/{Guid.NewGuid():D}"));
    }

    [TestMethod]
    public async Task TheMusicLibraryListsArtistsAlbumsAndTracksAndRequestsAMissingAlbumThroughTheSharedFlow()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", DuneRelease);
        await using var host = await VideoAdminPageHost.CreateAsync(video);
        var db = video.Environment.Db;
        var artist = new MusicArtist { Name = "Daft Punk", SortName = "Daft Punk", MusicBrainzId = "056e4f3e-d505-4dad-8ec1-d04f521cbb56", LastRefreshedAt = DateTime.UtcNow };
        var work = new Work { MediaType = WorkMediaType.Music, CanonicalTitle = "Homework", Year = 1997 };
        db.AddRange(artist, work);
        db.MusicAlbums.Add(new MusicAlbum { WorkId = work.Id, ArtistId = artist.Id, MusicBrainzReleaseGroupId = "rg-hw", ReleaseDate = new DateTime(1997, 1, 20, 0, 0, 0, DateTimeKind.Utc) });
        db.WorkTracks.Add(new WorkTrack { WorkId = work.Id, Number = 1, Title = "Daftendirekt" });
        await db.SaveChangesAsync();

        var library = await host.GetHtmlAsync("/Music");
        StringAssert.Contains(library, "Daft Punk");
        StringAssert.Contains(library, "1 albums, 0 in the library");
        var artistPage = await host.GetHtmlAsync($"/Music/Artist/{artist.Id:D}");
        StringAssert.Contains(artistPage, "Homework");
        var albumPage = await host.GetHtmlAsync($"/Music/Album/{work.Id:D}");
        StringAssert.Contains(albumPage, "Daftendirekt");
        StringAssert.Contains(albumPage, "Request album");
        Assert.IsFalse(albumPage.Contains("Play", StringComparison.Ordinal), "There is no Play action until Jularr plays music.");

        var adminLibrary = await host.GetHtmlAsync("/Admin/Music");
        StringAssert.Contains(adminLibrary, "Daft Punk");
        var adminArtist = await host.GetHtmlAsync($"/Admin/Music/Artist/{artist.Id:D}");
        StringAssert.Contains(adminArtist, "Homework");
        var adminAlbum = await host.GetHtmlAsync($"/Admin/Music/Album/{work.Id:D}");
        StringAssert.Contains(adminAlbum, "Daftendirekt");
        StringAssert.Contains(adminAlbum, "Manual search");

        Assert.AreEqual(HttpStatusCode.Redirect, await host.PostAsync($"/Music/Album/{work.Id:D}", $"/Music/Album/{work.Id:D}?handler=Request", []));
        var request = (await video.Get<AcquisitionAccessStore>().ListAsync(MediaAcquisitionKind.Music, null, openOnly: false, 10, CancellationToken.None)).Single();
        Assert.AreEqual("rg-hw", request.ExternalId);
        Assert.AreEqual(HttpStatusCode.NotFound, await host.GetStatusAsync($"/Music/Album/{Guid.NewGuid():D}"));
    }

    private static List<KeyValuePair<string, string>> FormFields(QualityProfileForm form)
    {
        List<KeyValuePair<string, string>> fields =
        [
            new("Id", form.Id),
            new("Name", form.Name),
            new("UpgradeAllowed", form.UpgradeAllowed ? "true" : "false"),
            new("UpgradeCutoffQuality", form.UpgradeCutoffQuality ?? ""),
            new("UpgradeMinimumQualitySteps", form.UpgradeMinimumQualitySteps ?? ""),
            new("UpgradeMinimumScoreDelta", form.UpgradeMinimumScoreDelta ?? ""),
            new("UpgradeUntilScore", form.UpgradeUntilScore ?? ""),
            new("MinimumScore", form.MinimumScore ?? ""),
            new("MinimumSizeMegabytes", form.MinimumSizeMegabytes ?? ""),
            new("MaximumSizeMegabytes", form.MaximumSizeMegabytes ?? ""),
            new("MustContain", form.MustContain),
            new("MustNotContain", form.MustNotContain),
            new("RequiredRegex", form.RequiredRegex),
            new("RejectedRegex", form.RejectedRegex)
        ];
        fields.AddRange(form.QualityOrder.Select(quality => new KeyValuePair<string, string>("QualityOrder", quality)));
        fields.AddRange(form.AllowedQualities.Select(quality => new KeyValuePair<string, string>("AllowedQualities", quality)));
        for (var index = 0; index < form.Rules.Count; index++)
        {
            var rule = form.Rules[index];
            fields.AddRange([new("Rules.Index", index.ToString()), new($"Rules[{index}].Effect", rule.Effect ?? ""), new($"Rules[{index}].Field", rule.Field ?? ""), new($"Rules[{index}].Match", rule.Match ?? ""), new($"Rules[{index}].Value", rule.Value ?? ""), new($"Rules[{index}].Score", rule.Score ?? ""), new($"Rules[{index}].Name", rule.Name ?? "")]);
        }

        for (var index = 0; index < form.Tiers.Count; index++)
        {
            fields.Add(new("Tiers.Index", index.ToString()));
            fields.Add(new($"Tiers[{index}].Minutes", form.Tiers[index].Minutes ?? ""));
            fields.AddRange(form.Tiers[index].Qualities.Select(quality => new KeyValuePair<string, string>($"Tiers[{index}].Qualities", quality)));
        }

        return fields;
    }
}
