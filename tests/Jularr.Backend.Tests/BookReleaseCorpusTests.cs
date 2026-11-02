using System.Globalization;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Logic.Acquisition;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class BookReleaseCorpusTests
    {
        private static readonly Dictionary<string, (MediaSubtype? Subtype, string Title, string Author)> Requests = new(StringComparer.Ordinal)
        {
            ["mushoku"] = (MediaSubtype.LightNovel, "Mushoku Tensei: Jobless Reincarnation", "Rifujin na Magonote"),
            ["waykings"] = (null, "The Way of Kings", "Brandon Sanderson"),
            ["pride"] = (null, "Pride and Prejudice", "Jane Austen")
        };

        private static readonly Dictionary<string, QualitySize> Sizes = new(StringComparer.OrdinalIgnoreCase)
        {
            [BookReleaseJudge.EbookSize] = new(BookReleaseJudge.EbookSize, 0, null, 150),
            [BookReleaseJudge.IllustratedSize] = new(BookReleaseJudge.IllustratedSize, 0, null, 300),
            [BookReleaseJudge.MangaSize] = new(BookReleaseJudge.MangaSize, 0, null, 700)
        };

        [TestMethod]
        public void Corpus_EveryRow_ParsesAndDecidesAsExpected()
        {
            var failures = new List<string>();
            foreach (var request in Corpus().GroupBy(row => row[0]))
            {
                var rows = request.ToArray();
                var result = Select(request.Key, rows);
                foreach (var row in rows)
                {
                    var actual = Describe(row, result);
                    if (!actual.SequenceEqual(row)) failures.Add("expected " + string.Join(" | ", row) + Environment.NewLine + "  actual " + string.Join(" | ", actual));
                }
            }

            Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
        }

        [TestMethod]
        public void Corpus_ObservedNyaaAnswers_HaveAScoreOrANamedRejection()
        {
            var rows = Corpus().Where(row => row[0] == "mushoku").Take(11).ToArray();
            var result = Select("mushoku", rows);
            Assert.HasCount(11, result.Ranked);
            foreach (var evaluation in result.Ranked)
            {
                Assert.IsTrue(evaluation.PreferenceScore > 0 || evaluation.Decision == SelectionDecision.Rejected && evaluation.Reasons[^1].Code.EndsWith("NotWanted", StringComparison.Ordinal),
                    evaluation.Candidate.Id);
            }

            // The constant "author not named" of the old judge is gone; the reasons differ by release.
            Assert.IsFalse(result.Ranked.Any(evaluation => evaluation.Reasons.Any(reason => reason.Code == "AuthorMissing")));
            Assert.IsGreaterThan(3, result.Ranked.Select(evaluation => evaluation.Reasons[^1].Code).Distinct().Count());
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(7)]
        [DataRow(42)]
        [DataRow(2026)]
        public void Corpus_AnyInputOrder_GivesTheSameScoresAndOrder(int seed)
        {
            foreach (var request in Corpus().GroupBy(row => row[0]))
            {
                var rows = request.ToArray();
                var shuffled = rows.ToArray();
                new Random(seed).Shuffle(shuffled);
                CollectionAssert.AreEqual(Ranking(Select(request.Key, rows)), Ranking(Select(request.Key, shuffled)), request.Key);
            }
        }

        [TestMethod]
        public void ManualSearch_SizeOutsideTheLimits_IsMarkedForAPersonInsteadOfRejected()
        {
            string[] row = ["pride", "Pride and Prejudice (EN) (epub)", "700.0"];
            var automatic = Select("pride", [row]).Ranked.Single();
            var manual = Select("pride", [row], automatic: false).Ranked.Single();

            Assert.AreEqual(SelectionDecision.Rejected, automatic.Decision);
            Assert.AreEqual(SelectionDecision.ManualReview, manual.Decision);
            Assert.AreEqual("SizeAboveVolumeMaximum", manual.Reasons[^1].Code);
            Assert.AreEqual("700 MB > 150 MB", manual.Reasons[^1].Value);
        }

        [TestMethod]
        public void SingleVolume_BeatsAPackOfTheSameQuality()
        {
            string[][] rows =
            [
                ["mushoku", "Mushoku Tensei: Jobless Reincarnation Vol 01-05 [Seven Seas] [Stick] (EN) (epub)", "60.0"],
                ["mushoku", "Mushoku Tensei: Jobless Reincarnation Vol 01 [Seven Seas] [Stick] (EN) (epub)", "12.0"]
            ];
            var result = Select("mushoku", rows);

            Assert.AreEqual(rows[1][1], result.Winner?.Candidate.Parsed?.RawTitle);
            Assert.AreEqual(SelectionDecision.Eligible, result.Ranked.Single(evaluation => evaluation.Candidate.Parsed!.RawTitle == rows[0][1]).Decision);
        }

        private static string[] Ranking(SelectionResult result) =>
            [.. result.Ranked.Select(evaluation => evaluation.Candidate.Id + " " + evaluation.Decision + " " + evaluation.PreferenceScore.ToString(CultureInfo.InvariantCulture))];

        // The columns of a row as the parser and the choice see them.
        private static string[] Describe(string[] row, SelectionResult result)
        {
            var evaluation = result.Ranked.Single(item => item.Candidate.Parsed?.RawTitle == row[1]);
            var name = BookReleaseJudge.ReadName(row[1]);
            return
            [
                row[0], row[1], row[2], name.Kind.ToString(), name.Format ?? "-",
                name.VolumeStart is not { } start ? "-" : name.VolumeEnd is { } end ? start + "-" + end : start.ToString(CultureInfo.InvariantCulture),
                Join(name.Editions), name.Publisher ?? "-", name.Group ?? "-", name.Year?.ToString(CultureInfo.InvariantCulture) ?? "-", Join(name.Languages),
                evaluation.Candidate.SizeMark?.ToString() ?? "-", evaluation.Decision.ToString(), evaluation.Reasons[^1].Code, evaluation.PreferenceScore.ToString(CultureInfo.InvariantCulture)
            ];

            static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "-" : string.Join(',', values);
        }

        private static SelectionResult Select(string request, IReadOnlyList<string[]> rows, bool automatic = true)
        {
            var (subtype, title, author) = Requests[request];
            var profile = new QualityProfile("1", "Books", [.. BookQualities.Order], [.. BookQualities.Order], false, null, null, null)
            {
                IsBook = true,
                LanguageOrder = ["EN"],
                Sizes = Sizes
            };
            var target = new WantedTargetData(1, 1, MediaType.Book, subtype, title, null, [title], 1, "Books", "{}", [], false, false, false, 0, [], ["en"], author, null, null);
            var offers = rows.Select(row => new ReleaseOffer(row[1], 1, "Nyaa", 25, row[1], new Uri("https://nyaa.example.test/download"), null,
                (long)(decimal.Parse(row[2], CultureInfo.InvariantCulture) * 1_048_576m), null, ["title"]) { Transport = DownloadTransport.Torrent });
            return AcquisitionSelectionLogic.Select(profile, target, AcquisitionSelectionLogic.GetKind(MediaType.Book, subtype), null, offers, automatic: automatic);
        }

        private static IEnumerable<string[]> Corpus()
        {
            using var stream = typeof(BookReleaseCorpusTests).Assembly.GetManifestResourceStream("ReleaseNames.book-releases.tsv")
                ?? throw new InvalidOperationException("The release corpus is missing.");
            using var reader = new StreamReader(stream);
            return [.. reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0 && line[0] != '#').Select(line => line.Split('\t'))];
        }
    }
}
