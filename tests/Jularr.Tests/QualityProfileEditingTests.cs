using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Prowlarr;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.ReadingAcquisition;

namespace Jularr.Tests;

/// <summary>The Admin profile editor reads and writes the one generic profile model; what it stores is what the shared engine enforces.</summary>
[TestClass]
public sealed class QualityProfileEditingTests
{
    private static ProwlarrReleaseCandidate Release(string title) =>
        new(title, "Test indexer", 1, "usenet", 100L * 1024 * 1024, null, null, DateTimeOffset.UtcNow, 0, 1, title, null, AnimeReleaseParser.Parse(title), [], new Uri($"https://indexer.invalid/{Uri.EscapeDataString(title)}"), null);

    [TestMethod]
    public void EveryDefaultProfileSurvivesTheRoundTripThroughItsFormUnchanged()
    {
        var registry = new MediaAcquisitionRegistry([new MovieAcquisitionRegistration(), new TvAcquisitionRegistration(), new MangaAcquisitionRegistration(), new LightNovelAcquisitionRegistration(), new BookAcquisitionRegistration()]);

        foreach (var kind in registry.Kinds)
        {
            var profile = registry.DefaultProfileFor(kind);

            var result = QualityProfileEditing.Parse(QualityProfileEditing.ToForm(profile));

            Assert.IsTrue(result.IsValid, $"{kind}: {string.Join("; ", result.Errors.Select(error => $"{error.Field} {error.Code} {error.Detail}"))}");
            var parsed = result.Profile!;
            CollectionAssert.AreEqual(profile.QualityOrder, parsed.QualityOrder, kind.ToString());
            CollectionAssert.AreEqual(profile.AllowedQualities, parsed.AllowedQualities, kind.ToString());
            Assert.AreEqual(profile.UpgradeAllowed, parsed.UpgradeAllowed, kind.ToString());
            Assert.AreEqual(profile.UpgradeCutoffQuality, parsed.UpgradeCutoffQuality, kind.ToString());
            Assert.AreEqual(profile.ScoreRules.Length, parsed.ScoreRules.Length, kind.ToString());
            Assert.AreEqual(profile.FallbackTiers.Length, parsed.FallbackTiers.Length, kind.ToString());
        }
    }

    [TestMethod]
    public void AnEditedMangaProfileChangesWhichReleaseTheSharedEngineTakesFirst()
    {
        var target = new ReadingAcquisitionTarget(MediaAcquisitionKind.Manga, "Frieren", []);
        var releases = new[] { Release("Frieren Vol 01 CBZ-AAA"), Release("Frieren Vol 01 CBZ-GOOD") };
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultManga());
        foreach (var preferred in new[] { "AAA", "GOOD" })
        {
            form.ScoreRules = $"Prefer | ReleaseGroup | Equals | {preferred} | 20 | Preferred group";
            var edited = QualityProfileEditing.Parse(form);

            Assert.IsTrue(edited.IsValid);
            Assert.AreEqual($"Frieren Vol 01 CBZ-{preferred}", ReadingReleaseSelector.Rank(releases, target, edited.Profile)[0].Release.Title, "The rule the owner wrote decides between otherwise equal releases.");
        }
    }

    [TestMethod]
    public void ARejectRuleAndASizeLimitKeepAReleaseOutOfTheEngineForGood()
    {
        var target = new ReadingAcquisitionTarget(MediaAcquisitionKind.Manga, "Frieren", []);
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultManga());
        form.ScoreRules = "Reject | ReleaseGroup | Equals | BAD | 0 | Never this group";
        form.MaximumSizeMegabytes = "50";

        var profile = QualityProfileEditing.Parse(form).Profile!;
        var ranked = ReadingReleaseSelector.Rank([Release("Frieren Vol 01 CBZ-BAD"), Release("Frieren Vol 02 CBZ-OK")], target, profile);

        Assert.AreEqual(0, ranked.Single(item => item.Release.Title.EndsWith("BAD", StringComparison.Ordinal)).Score, "A rejecting rule is a gate, not a penalty.");
        Assert.AreEqual(0, ranked.Single(item => item.Release.Title.EndsWith("OK", StringComparison.Ordinal)).Score, "The 100 MB release is above the 50 MB limit.");
        Assert.AreEqual(50L * 1024 * 1024, profile.MaximumSizeBytes);
    }

    [TestMethod]
    public void WhateverCannotBeReadIsReportedWithItsFieldAndLineAndNothingIsStored()
    {
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultManga());
        form.ScoreRules = "Prefer | ReleaseGroup | Equals | GOOD | 20 | fine\nPrefer | NoSuchField | Equals | x | 1 | broken";
        form.FallbackTiers = "soon: ZIP\n30: PDF";
        form.UpgradeMinimumScoreDelta = "many";

        var result = QualityProfileEditing.Parse(form);

        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.Profile);
        Assert.Contains(error => error is { Field: nameof(QualityProfileForm.ScoreRules), Line: 2, Code: "rule" }, result.Errors);
        Assert.Contains(error => error is { Field: nameof(QualityProfileForm.FallbackTiers), Line: 1, Code: "tier" }, result.Errors);
        Assert.Contains(error => error is { Field: nameof(QualityProfileForm.UpgradeMinimumScoreDelta), Code: "number" }, result.Errors);
    }

    [TestMethod]
    public void AWaitingStepMayOnlyNameQualitiesOfTheOrderAndTheCutoffMustBeInIt()
    {
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultLightNovel());
        form.FallbackTiers = "60: MOBI";
        Assert.Contains(error => error.Code == "quality" && error.Detail == "MOBI", QualityProfileEditing.Parse(form).Errors);

        form.FallbackTiers = "60: ZIP";
        form.UpgradeCutoffQuality = "AZW3";

        var result = QualityProfileEditing.Parse(form);
        Assert.IsFalse(result.IsValid);
        Assert.Contains(error => error.Code == "profile", result.Errors);
    }
}
