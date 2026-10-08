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
            CollectionAssert.AreEqual(profile.AllowedQualities.Length == 0 ? profile.QualityOrder : profile.AllowedQualities, parsed.AllowedQualities, "A profile without allowed qualities takes the whole order, which the form shows ticked: " + kind);
            Assert.AreEqual(profile.UpgradeAllowed, parsed.UpgradeAllowed, kind.ToString());
            Assert.AreEqual(profile.UpgradeCutoffQuality, parsed.UpgradeCutoffQuality, kind.ToString());
            Assert.AreEqual(profile.ScoreRules.Length, parsed.ScoreRules.Length, kind.ToString());
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
            form.Rules = [new ScoreRuleRow { Effect = "Prefer", Field = "ReleaseGroup", Match = "Equals", Value = preferred, Name = "Preferred group" }];
            var edited = QualityProfileEditing.Parse(form);

            Assert.IsTrue(edited.IsValid);
            Assert.AreEqual($"Frieren Vol 01 CBZ-{preferred}", ReadingRank.Rank(releases, target, edited.Profile)[0].Release.Title, "The rule the owner wrote decides between otherwise equal releases.");
        }
    }

    [TestMethod]
    public void ARejectRuleAndASizeLimitKeepAReleaseOutOfTheEngineForGood()
    {
        var target = new ReadingAcquisitionTarget(MediaAcquisitionKind.Manga, "Frieren", []);
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultManga());
        form.Rules = [new ScoreRuleRow { Effect = "Reject", Field = "ReleaseGroup", Match = "Equals", Value = "BAD", Name = "Never this group" }];
        form.MaximumSizeMegabytes = "50";

        var profile = QualityProfileEditing.Parse(form).Profile!;
        var ranked = ReadingRank.Rank([Release("Frieren Vol 01 CBZ-BAD"), Release("Frieren Vol 02 CBZ-OK")], target, profile);

        Assert.AreEqual(0, ranked.Single(item => item.Release.Title.EndsWith("BAD", StringComparison.Ordinal)).Score, "A rejecting rule is a gate, not a penalty.");
        Assert.AreEqual(0, ranked.Single(item => item.Release.Title.EndsWith("OK", StringComparison.Ordinal)).Score, "The 100 MB release is above the 50 MB limit.");
        Assert.AreEqual(50L * 1024 * 1024, profile.MaximumSizeBytes);
    }

    [TestMethod]
    public void WhateverCannotBeReadIsReportedWithItsFieldAndLineAndNothingIsStored()
    {
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultManga());
        form.Rules =
        [
            new ScoreRuleRow { Effect = "Prefer", Field = "ReleaseGroup", Match = "Equals", Value = "GOOD", Name = "fine" },
            new ScoreRuleRow { Effect = "Prefer", Field = "NoSuchField", Match = "Equals", Value = "x", Name = "broken" },
            new ScoreRuleRow()
        ];
        form.UpgradeMinimumQualitySteps = "many";

        var result = QualityProfileEditing.Parse(form);

        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.Profile);
        Assert.Contains(error => error is { Field: nameof(QualityProfileForm.Rules), Line: 2, Code: "rule" }, result.Errors);
        Assert.DoesNotContain(error => error.Field == nameof(QualityProfileForm.Rules) && error.Line == 3, result.Errors, "The blank row the editor offers for adding a rule is not an error.");
        Assert.Contains(error => error is { Field: nameof(QualityProfileForm.UpgradeMinimumQualitySteps), Code: "number" }, result.Errors);
    }

    [TestMethod]
    public void AProfileThatTakesNoQualityAtOnceIsRefusedInsteadOfBecomingOneThatTakesEveryQuality()
    {
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultManga());
        form.AllowedQualities = [];

        var result = QualityProfileEditing.Parse(form);

        Assert.Contains(error => error is { Field: nameof(QualityProfileForm.AllowedQualities), Code: "allowed" }, result.Errors);
    }

    [TestMethod]
    public void TheCutoffMustBeOneOfTheQualitiesOfTheOrder()
    {
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultLightNovel());
        form.UpgradeCutoffQuality = "AZW3";

        var result = QualityProfileEditing.Parse(form);

        Assert.IsFalse(result.IsValid);
        Assert.Contains(error => error.Code == "profile", result.Errors);
    }

    [TestMethod]
    public void TheOrderOfTheRulesIsTheirPriorityAndNoNumberIsAsked()
    {
        var target = new ReadingAcquisitionTarget(MediaAcquisitionKind.Manga, "Frieren", []);
        var releases = new[] { Release("Frieren Vol 01 CBZ-AAA"), Release("Frieren Vol 01 CBZ-GOOD") };
        var form = QualityProfileEditing.ToForm(ReadingQualityProfiles.CreateDefaultManga());
        ScoreRuleRow Prefer(string group) => new() { Effect = "Prefer", Field = "ReleaseGroup", Match = "Equals", Value = group, Name = group };

        form.Rules = [Prefer("GOOD"), Prefer("AAA")];
        var goodFirst = QualityProfileEditing.Parse(form).Profile!;
        form.Rules = [Prefer("AAA"), Prefer("GOOD")];
        var aaaFirst = QualityProfileEditing.Parse(form).Profile!;

        Assert.AreEqual("Frieren Vol 01 CBZ-GOOD", ReadingRank.Rank(releases, target, goodFirst)[0].Release.Title);
        Assert.AreEqual("Frieren Vol 01 CBZ-AAA", ReadingRank.Rank(releases, target, aaaFirst)[0].Release.Title);
        CollectionAssert.AreEqual(new[] { "GOOD", "AAA" }, QualityProfileEditing.ToForm(goodFirst).Rules.Select(row => row.Value).ToArray(), "The editor shows the rules in the order they rank.");
    }
}
