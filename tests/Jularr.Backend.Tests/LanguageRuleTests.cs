using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Logic.Acquisition;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    // The add-ons of a language (preferred and blocked release groups and words) in a search of a film, and their administration.
    [TestClass]
    public sealed class LanguageRuleTests
    {
        private const string Rules = "/api/v1/admin/acquisition/language-rules";

        private static SelectionResult Search(bool automatic, string rulesJson, params string[] titles)
        {
            var definition = JsonSerializer.Serialize(new AcquisitionProfileDefinition(["WEBDL-1080p"], ["WEBDL-1080p"], false, null, null, null));
            var target = new WantedTargetData(1, 1, MediaType.Movie, null, "Example Movie", 2020, ["Example Movie"], 1, "Movies", definition, [], false, false, false, 0, [], ["de"], null, null, null,
                originalLanguage: "ja", languageChains: ["de|en|1"], languageRules: rulesJson);
            var offers = titles.Select(title => new ReleaseOffer(title, 1, "Indexer", 0, title, new Uri("https://example.org/" + Uri.EscapeDataString(title)), null, 4_000_000_000, null, ["title"]));
            return AcquisitionSelectionLogic.Select(AcquisitionSelectionLogic.ReadProfile(target), target, AcquisitionKind.Movie, null, offers, automatic: automatic);
        }

        private static CandidateEvaluation Row(SelectionResult result, string title) => result.Ranked.Single(evaluation => evaluation.Candidate.Parsed?.RawTitle == title);

        private const string Plain = "Example.Movie.2020.German.1080p.WEB-DL.H264-AAA";
        private const string Preferred = "Example.Movie.2020.German.1080p.WEB-DL.H264-GRP";
        private const string Blocked = "Example.Movie.2020.German.1080p.WEB-DL.H264-BAD";
        private const string Rule = """[{"Language":"de","PreferredGroups":["GRP"],"BlockedGroups":["BAD"],"PreferredWords":[],"BlockedWords":[]}]""";

        [TestMethod]
        public void Search_PreferredGroupOfTheReleasesLanguage_WinsAndNamesItsPoints()
        {
            var result = Search(true, Rule, Plain, Preferred);

            Assert.AreEqual(Preferred, result.Winner!.Candidate.Parsed!.RawTitle);
            var points = Row(result, Preferred).Candidate.ContextPoints.Single(point => point.Code == "LanguageGroupPreferred");
            Assert.AreEqual(("GRP", LanguageRuleLogic.GroupPoints), (points.Value, points.Points));
        }

        [TestMethod]
        public void Search_BlockedGroup_IsRejectedAutomatically_AndOnlyMarkedByHand()
        {
            var automatic = Row(Search(true, Rule, Plain, Blocked), Blocked);
            Assert.AreEqual(SelectionDecision.Rejected, automatic.Decision);
            Assert.AreEqual("LanguageGroupBlocked", automatic.Reasons[^1].Code);

            var manual = Row(Search(false, Rule, Plain, Blocked), Blocked);
            Assert.AreEqual(SelectionDecision.ManualReview, manual.Decision);
            Assert.AreEqual("LanguageGroupBlocked", manual.Reasons[^1].Code);
        }

        [TestMethod]
        public void Search_ARuleOfAnotherLanguage_ChangesNothing()
        {
            var result = Search(true, Rule.Replace("\"de\"", "\"fr\""), Preferred, Blocked);

            Assert.AreEqual(SelectionDecision.Eligible, Row(result, Blocked).Decision);
            Assert.AreEqual(0, Row(result, Preferred).Candidate.ContextPoints.Count(point => point.Code == "LanguageGroupPreferred"));
        }

        [TestMethod]
        public async Task Service_SavesTrimmedEntriesOnce_ReadsThemBack_AndAnEmptyRuleIsRemoved()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

            using var saved = await host.SendAsync(HttpMethod.Put, Rules + "/de", new SaveLanguageRuleDataV1([" GRP ", "grp", "Other"], ["BAD"], ["Remux"], []));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            using var read = await host.SendAsync(HttpMethod.Get, Rules);
            var rule = (await read.Content.ReadFromJsonAsync<BaseDataList<LanguageRuleV1>>())!.Single();
            Assert.AreEqual("de", rule.Language);
            CollectionAssert.AreEqual(new[] { "GRP", "Other" }, rule.PreferredGroups);
            CollectionAssert.AreEqual(new[] { "BAD" }, rule.BlockedGroups);
            CollectionAssert.AreEqual(new[] { "Remux" }, rule.PreferredWords);
            Assert.AreEqual(0, rule.BlockedWords.Length);

            using var cleared = await host.SendAsync(HttpMethod.Put, Rules + "/de", new SaveLanguageRuleDataV1([], [], [], []));
            Assert.AreEqual(HttpStatusCode.OK, cleared.StatusCode, await cleared.Content.ReadAsStringAsync());
            using var empty = await host.SendAsync(HttpMethod.Get, Rules);
            Assert.AreEqual(0, (await empty.Content.ReadFromJsonAsync<BaseDataList<LanguageRuleV1>>())!.Count());

            using var refused = await host.SendAsync(HttpMethod.Put, Rules + "/german", new SaveLanguageRuleDataV1(["GRP"], [], [], []));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }
}
