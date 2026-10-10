using Jularr.Web.Features.Collections;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Ui;

namespace Jularr.Tests;

/// <summary>
/// Covers the pure core of smart collections (#427): nested ALL/ANY evaluation, cross-media membership,
/// explainability and rule serialization. All in-memory, so no database or provider is touched.
/// </summary>
[TestClass]
public sealed class CollectionRuleTests
{
    private static WorkFactSnapshot Work(
        WorkMediaType mediaType,
        string title,
        int? year = null,
        MediaReleaseStatus? status = null,
        int? primaryUnits = null,
        int? secondaryUnits = null,
        (string Code, bool Complete)[]? languages = null,
        Guid[]? franchises = null) =>
        new(
            Random.Shared.NextInt64(1, long.MaxValue),
            mediaType,
            title,
            year,
            status,
            primaryUnits,
            secondaryUnits,
            null,
            [.. (languages ?? []).Select(x => new WorkFactLanguage(x.Code, x.Complete))],
            franchises ?? []);

    [TestMethod]
    public void NestedAllRequiresEveryChildToMatch()
    {
        var rule = CollectionRuleGroup.All(
            new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.Equals, "anime"),
            CollectionRuleGroup.Any(
                new CollectionRuleCondition(CollectionRuleField.ReleaseYear, CollectionRuleOperator.GreaterThanOrEqual, "2020"),
                new CollectionRuleCondition(CollectionRuleField.Status, CollectionRuleOperator.Equals, "finished")));

        // Anime + recent → matches (media type, and the ANY subtree via year).
        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(rule, Work(WorkMediaType.Anime, "A", year: 2023)).Matched);
        // Anime + old but finished → matches (ANY subtree via status).
        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(
            rule, Work(WorkMediaType.Anime, "B", year: 2001, status: MediaReleaseStatus.Finished)).Matched);
        // Anime + old + ongoing → the ANY subtree fails, so ALL fails.
        Assert.IsFalse(CollectionRuleEvaluator.Evaluate(
            rule, Work(WorkMediaType.Anime, "C", year: 2001, status: MediaReleaseStatus.Ongoing)).Matched);
        // Recent, but a book → the media-type child fails, so ALL fails.
        Assert.IsFalse(CollectionRuleEvaluator.Evaluate(rule, Work(WorkMediaType.Book, "D", year: 2023)).Matched);
    }

    [TestMethod]
    public void AnyGroupMatchesAcrossMediaTypes()
    {
        // A cross-media rule: anime OR books.
        var rule = CollectionRuleGroup.Any(
            new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.Equals, "anime"),
            new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.Equals, "book"));

        var members = SmartCollectionMaterializer.Match(rule,
        [
            Work(WorkMediaType.Anime, "Anime one", year: 2020),
            Work(WorkMediaType.Book, "Book one", year: 2021),
            Work(WorkMediaType.Manga, "Manga one", year: 2019)
        ]);

        CollectionAssert.AreEquivalent(
            new[] { "Anime one", "Book one" },
            members.Select(m => m.Work.Title).ToArray());
    }

    [TestMethod]
    public void EmptyAllGroupIsVacuouslyTrueAndEmptyAnyMatchesNothing()
    {
        var all = CollectionRuleGroup.All();
        var any = CollectionRuleGroup.Any();
        var work = Work(WorkMediaType.Anime, "X");

        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(all, work).Matched, "An empty ALL group is vacuously true.");
        Assert.IsFalse(CollectionRuleEvaluator.Evaluate(any, work).Matched, "An empty ANY group matches nothing.");
    }

    [TestMethod]
    public void MatchExplainsWhyEachConditionHeld()
    {
        var rule = CollectionRuleGroup.All(
            new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.Equals, "anime"),
            new CollectionRuleCondition(CollectionRuleField.Language, CollectionRuleOperator.Contains, "ja"));

        var result = CollectionRuleEvaluator.Evaluate(
            rule,
            Work(WorkMediaType.Anime, "Frieren", languages: [("ja", true)]));

        Assert.IsTrue(result.Matched);
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("Media type") && r.Contains("Anime")), string.Join(" | ", result.Reasons));
        Assert.IsTrue(result.Reasons.Any(r => r.Contains("Language") && r.Contains("JA")), string.Join(" | ", result.Reasons));
    }

    [TestMethod]
    public void LanguageCompleteOnlyMatchesFullCoverage()
    {
        var rule = new CollectionRuleCondition(CollectionRuleField.LanguageComplete, CollectionRuleOperator.Contains, "de");

        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(
            rule, Work(WorkMediaType.Anime, "Full", languages: [("de", true)])).Matched);
        Assert.IsFalse(CollectionRuleEvaluator.Evaluate(
            rule, Work(WorkMediaType.Anime, "Partial", languages: [("de", false)])).Matched,
            "A partially covered language must not satisfy a complete-coverage rule.");
    }

    [TestMethod]
    public void FranchiseAndPresenceConditionsMatch()
    {
        var franchiseId = Guid.NewGuid();
        var inFranchise = new CollectionRuleCondition(CollectionRuleField.Franchise, CollectionRuleOperator.Contains, franchiseId.ToString());
        var hasFranchise = new CollectionRuleCondition(CollectionRuleField.HasFranchise, CollectionRuleOperator.IsPresent, "");
        var noFranchise = new CollectionRuleCondition(CollectionRuleField.HasFranchise, CollectionRuleOperator.IsAbsent, "");

        var member = Work(WorkMediaType.Anime, "Member", franchises: [franchiseId]);
        var loner = Work(WorkMediaType.Anime, "Loner");

        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(inFranchise, member).Matched);
        Assert.IsFalse(CollectionRuleEvaluator.Evaluate(inFranchise, loner).Matched);
        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(hasFranchise, member).Matched);
        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(noFranchise, loner).Matched);
    }

    [TestMethod]
    public void NumberOperatorsRespectPresenceAndComparison()
    {
        var atLeastTwelve = new CollectionRuleCondition(CollectionRuleField.PrimaryUnitCount, CollectionRuleOperator.GreaterThanOrEqual, "12");
        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(atLeastTwelve, Work(WorkMediaType.Anime, "Long", primaryUnits: 24)).Matched);
        Assert.IsFalse(CollectionRuleEvaluator.Evaluate(atLeastTwelve, Work(WorkMediaType.Anime, "Short", primaryUnits: 6)).Matched);
        // A missing count cannot satisfy a numeric comparison, but IsAbsent captures it.
        Assert.IsFalse(CollectionRuleEvaluator.Evaluate(atLeastTwelve, Work(WorkMediaType.Anime, "Unknown")).Matched);
        var missing = new CollectionRuleCondition(CollectionRuleField.PrimaryUnitCount, CollectionRuleOperator.IsAbsent, "");
        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(missing, Work(WorkMediaType.Anime, "Unknown")).Matched);
    }

    [TestMethod]
    public void MaterializeMatchOrdersNewestFirstAndSkipsNullRule()
    {
        var rule = new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.Equals, "anime");
        var members = SmartCollectionMaterializer.Match(rule,
        [
            Work(WorkMediaType.Anime, "Old", year: 2005),
            Work(WorkMediaType.Anime, "New", year: 2024),
            Work(WorkMediaType.Anime, "Mid", year: 2015)
        ]);

        CollectionAssert.AreEqual(
            new[] { "New", "Mid", "Old" },
            members.Select(m => m.Work.Title).ToArray());

        Assert.AreEqual(0, SmartCollectionMaterializer.Match(null, [Work(WorkMediaType.Anime, "X")]).Count,
            "A smart collection with no rule matches nothing rather than the whole library.");
    }

    [TestMethod]
    public void RuleTreeRoundTripsThroughJson()
    {
        var rule = CollectionRuleGroup.All(
            new CollectionRuleCondition(CollectionRuleField.MediaType, CollectionRuleOperator.NotEquals, "book"),
            CollectionRuleGroup.Any(
                new CollectionRuleCondition(CollectionRuleField.ReleaseYear, CollectionRuleOperator.GreaterThan, "2010"),
                new CollectionRuleCondition(CollectionRuleField.LanguageComplete, CollectionRuleOperator.Contains, "de")));

        var json = CollectionRuleSerializer.Serialize(rule);
        var restored = CollectionRuleSerializer.Deserialize(json);

        Assert.IsNotNull(restored);
        // Round-tripped rule evaluates identically to the original.
        var sample = Work(WorkMediaType.Anime, "Sample", year: 2015, languages: [("de", true)]);
        Assert.AreEqual(
            CollectionRuleEvaluator.Evaluate(rule, sample).Matched,
            CollectionRuleEvaluator.Evaluate(restored, sample).Matched);
        Assert.IsTrue(CollectionRuleEvaluator.Evaluate(restored, sample).Matched);
        Assert.IsNull(CollectionRuleSerializer.Deserialize("not json"), "Malformed rule JSON deserializes to null, not a throw.");
    }
}
