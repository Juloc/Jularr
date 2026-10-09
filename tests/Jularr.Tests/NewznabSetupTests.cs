using System.Net;
using System.Text;
using System.Xml.Linq;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Health;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Search;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

[TestClass]
public sealed class NewznabSetupTests
{
    private const string ApiKey = "secret-api-key-123";
    private const string Result = "[Group] Berserk - 01 WEB-DL 1080p AAC";

    private const string StandardCaps = """
        <caps>
          <server version="1.0" title="Treasure Maps" />
          <limits max="100" default="50" />
          <searching>
            <search available="yes" supportedParams="q" />
            <tv-search available="yes" supportedParams="q,tvdbid,season,ep" />
            <movie-search available="yes" supportedParams="q,imdbid" />
          </searching>
          <categories>
            <category id="2000" name="Movies"><subcat id="2040" name="Movies/HD" /></category>
            <category id="3000" name="Audio"><subcat id="3010" name="Audio/MP3" /><subcat id="3030" name="Audio/Audiobook" /><subcat id="3040" name="Audio/Lossless" /></category>
            <category id="5000" name="TV"><subcat id="5040" name="TV/HD" /><subcat id="5070" name="TV/Anime" /></category>
            <category id="7000" name="Books"><subcat id="7020" name="Books/Ebook" /><subcat id="7030" name="Books/Comics" /></category>
          </categories>
        </caps>
        """;

    private static string Rss(params string[] titles) =>
        "<rss version=\"2.0\" xmlns:newznab=\"http://www.newznab.com/DTD/2010/feeds/attributes/\"><channel>"
        + string.Concat(titles.Select(title => $"<item><title>{title}</title><guid>{Guid.NewGuid():N}</guid><enclosure url=\"https://indexer.example/getnzb/{Guid.NewGuid():N}.nzb\" length=\"1000\" /></item>"))
        + "</channel></rss>";

    private static string Error(int code, string description) => $"<error code=\"{code}\" description=\"{description}\" />";

    private static IndexerCapabilities Caps(string xml) => NewznabCapsParser.Parse(XDocument.Parse(xml), DateTimeOffset.UtcNow);

    private static IndexerEntry Entry(string name, string baseUrl = "https://indexer.example", IndexerCapabilities? capabilities = null, Dictionary<string, int[]>? byKind = null, int priority = 1) =>
        new(Guid.NewGuid(), name, IndexerType.Newznab, Enabled: true, priority, new IndexerSettings(baseUrl, [5070], [], 100) { Capabilities = capabilities, CategoriesByKind = byKind }, ApiKey);

    private sealed class Server(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public IEnumerable<Uri> Searches => Requests.Where(uri => Param(uri, "t") != "caps");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }

        public static string? Param(Uri uri, string name) => Pairs(uri).Where(pair => pair.Key == name).Select(pair => pair.Value).FirstOrDefault();

        public static int Count(Uri uri, string name) => Pairs(uri).Count(pair => pair.Key == name);

        private static IEnumerable<KeyValuePair<string, string>> Pairs(Uri uri) =>
            uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).Select(part => new KeyValuePair<string, string>(Uri.UnescapeDataString(part[0]), part.Length > 1 ? Uri.UnescapeDataString(part[1].Replace('+', ' ')) : ""));
    }

    private static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    private sealed class World : IDisposable
    {
        public World(Server server)
        {
            Server = server;
            Directory = SabnzbdTestSupport.CreateTemporaryDirectory();
            Store = new IndexerStore(new EphemeralDataProtectionProvider(), Directory);
            Health = new AcquisitionHealthStore(Directory);
            Indexer = new NewznabIndexer(new HttpClient(server), ProviderTestFactory.NewExecutor());
            Indexers = new Dictionary<IndexerType, IIndexer> { [IndexerType.Newznab] = Indexer };
            Setup = new IndexerSetupService(Store, Indexers, Health);
            Coordinator = new IndexerSearchCoordinator(Indexers, Store, Health, NullLogger<IndexerSearchCoordinator>.Instance);
        }

        public Server Server { get; }

        public DirectoryInfo Directory { get; }

        public IndexerStore Store { get; }

        public AcquisitionHealthStore Health { get; }

        public NewznabIndexer Indexer { get; }

        public IReadOnlyDictionary<IndexerType, IIndexer> Indexers { get; }

        public IndexerSetupService Setup { get; }

        public IndexerSearchCoordinator Coordinator { get; }

        public void Dispose() => Directory.Delete(recursive: true);
    }

    private static Server CapsServer(Func<Uri, HttpResponseMessage>? search = null, string caps = StandardCaps) =>
        new(uri => Server.Param(uri, "t") == "caps" ? Xml(caps) : search?.Invoke(uri) ?? Xml(Rss(Result)));

    [TestMethod]
    public async Task AddingAnIndexerNeedsOnlyTheAddressAndTheKeyAndStoresWhatWasDetected()
    {
        using var world = new World(CapsServer());

        var result = await world.Setup.AddAsync("indexer.example/api/", $" {ApiKey} ", null, CancellationToken.None);

        Assert.AreEqual(IndexerSetupOutcome.Added, result.Outcome);
        var reloaded = (await new IndexerStore(new EphemeralDataProtectionProvider(), world.Directory).LoadAllAsync()).Single();
        Assert.AreEqual("Treasure Maps", reloaded.Name, "The name the indexer reports about itself.");
        Assert.AreEqual("https://indexer.example", reloaded.Settings.BaseUrl, "The scheme is added and a trailing /api is not stored.");
        Assert.IsTrue(reloaded.Enabled);
        Assert.IsTrue(reloaded.Settings.Capabilities!.Supports(IndexerSearchMode.TvSearch, "tvdbid"));
        Assert.AreEqual(100, reloaded.Settings.Capabilities.MaximumLimit);
        Assert.AreEqual(IndexerReadinessLevel.Ready, IndexerReadiness.Level(reloaded));
        Assert.IsTrue(reloaded.Settings.Verification!.Answered(IndexerSearchMode.Search));
        Assert.IsTrue(reloaded.Settings.Verification.Answered(IndexerSearchMode.TvSearch), "Every advertised function was asked once.");
        Assert.IsTrue(world.Server.Searches.All(uri => Server.Param(uri, "limit") == "1" && !Server.Param(uri, "t")!.Contains("get", StringComparison.Ordinal)), "Validation reads one release and downloads nothing.");
        Assert.IsTrue(world.Server.Requests.Count <= 1 + 3, "One caps request and at most one probe per advertised function.");
        Assert.IsTrue(IndexerCategoryMapper.Resolve(MediaAcquisitionKind.Movie, reloaded) is { IsAvailable: true, Evidence: IndexerCategoryEvidence.Name or IndexerCategoryEvidence.Standard }, "The categories come from the caps; none was typed anywhere.");
    }

    [TestMethod]
    public async Task AFailedSetupExplainsTheCauseSavesNothingAndNeverShowsTheKey()
    {
        var cases = new (Func<Uri, HttpResponseMessage> Caps, IndexerCheckState State)[]
        {
            (_ => Xml(Error(100, "Incorrect user credentials")), IndexerCheckState.AuthenticationFailed),
            (_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), IndexerCheckState.AuthenticationFailed),
            (_ => new HttpResponseMessage(HttpStatusCode.NotFound), IndexerCheckState.Unavailable),
            (_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), IndexerCheckState.Unavailable),
            (_ => Xml("<html><body>Login</body></html>"), IndexerCheckState.InvalidResponse),
            (_ => Xml("<rss><channel/></rss>"), IndexerCheckState.InvalidResponse),
            (_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), IndexerCheckState.RateLimited)
        };
        foreach (var (caps, expected) in cases)
        {
            using var world = new World(new Server(caps));

            var result = await world.Setup.AddAsync("https://indexer.example", ApiKey, "Mine", CancellationToken.None);

            Assert.AreEqual(IndexerSetupOutcome.ConnectionFailed, result.Outcome);
            Assert.AreEqual(expected, result.State, result.Detail);
            Assert.IsFalse(result.Detail?.Contains(ApiKey, StringComparison.Ordinal) ?? false, "The key is never part of a message.");
            Assert.IsEmpty(await world.Store.LoadAllAsync(), "A connection that was not proven is not stored.");
        }
    }

    [TestMethod]
    [DataRow("", false)]
    [DataRow("ftp://indexer.example", false)]
    [DataRow("https://user:pass@indexer.example", false)]
    [DataRow("https://indexer.example/api?apikey=abc", false)]
    [DataRow("indexer.example", true)]
    [DataRow("https://indexer.example/newznab/api/", true)]
    public void TheAddressIsNormalizedWithoutLosingACustomPath(string raw, bool valid)
    {
        Assert.AreEqual(valid, IndexerSetupService.TryNormalizeBaseUrl(raw, out var normalized, out var problem), problem);
        if (valid)
        {
            Assert.AreEqual(raw.Contains("newznab", StringComparison.Ordinal) ? "https://indexer.example/newznab" : "https://indexer.example", normalized);
        }
    }

    [TestMethod]
    public async Task AnAddressWithoutSchemeFallsBackToHttpForAnIndexerThatOnlySpeaksIt()
    {
        var server = new Server(uri => uri.Scheme == "https" ? throw new HttpRequestException("no TLS") : Server.Param(uri, "t") == "caps" ? Xml(StandardCaps) : Xml(Rss(Result)));
        using var world = new World(server);

        var result = await world.Setup.AddAsync("indexer.local:8299", ApiKey, null, CancellationToken.None);

        Assert.AreEqual(IndexerSetupOutcome.Added, result.Outcome);
        Assert.AreEqual("http://indexer.local:8299", result.Entry!.Settings.BaseUrl);
        var explicitHttps = await world.Setup.AddAsync("https://other.local:8299", ApiKey, null, CancellationToken.None);
        Assert.AreEqual(IndexerSetupOutcome.ConnectionFailed, explicitHttps.Outcome, "A scheme the owner typed is respected, not replaced.");
    }

    private const string StructuredCaps = """
        <caps>
          <server title="Strict Indexer" />
          <searching>
            <search available="yes" supportedParams="q" />
            <tv-search available="yes" supportedParams="q,tvdbid,season,ep" />
            <movie-search available="yes" supportedParams="imdbid,tmdbid" />
            <book-search available="yes" supportedParams="title,author" />
            <audio-search available="yes" supportedParams="artist,album" />
          </searching>
          <categories><category id="2000" name="Movies" /></categories>
        </caps>
        """;

    [TestMethod]
    public async Task StructuredFunctionsAreProbedOnlyWithAnAdvertisedTextParameterAndNeverWithAnInventedIdentifier()
    {
        // Like a strict indexer: a structured request without any text parameter is refused, so a bare probe would have called a working function broken.
        var server = new Server(uri => Server.Param(uri, "t") switch
        {
            "caps" => Xml(StructuredCaps),
            "search" => Xml(Rss(Result)),
            _ when Server.Param(uri, "q") is null && Server.Param(uri, "title") is null && Server.Param(uri, "artist") is null => Xml(Error(200, "Missing parameter")),
            _ => Xml(Rss(Result))
        });
        using var world = new World(server);

        var result = await world.Setup.AddAsync("https://indexer.example", ApiKey, null, CancellationToken.None);

        var verification = result.Entry!.Settings.Verification!;
        Assert.IsTrue(result.Entry.Enabled);
        Assert.AreEqual(IndexerReadinessLevel.Ready, IndexerReadiness.Level(result.Entry));
        Assert.IsTrue(verification.Answered(IndexerSearchMode.TvSearch));
        Assert.IsTrue(verification.Answered(IndexerSearchMode.Book));
        Assert.IsTrue(verification.Answered(IndexerSearchMode.Music));
        var tv = server.Searches.Single(uri => Server.Param(uri, "t") == "tvsearch");
        Assert.AreEqual("test", Server.Param(tv, "q"));
        Assert.AreEqual("test", Server.Param(server.Searches.Single(uri => Server.Param(uri, "t") == "book"), "title"));
        Assert.AreEqual("test", Server.Param(server.Searches.Single(uri => Server.Param(uri, "t") == "music"), "artist"));
        Assert.IsTrue(server.Searches.All(uri => Server.Count(uri, "tvdbid") + Server.Count(uri, "imdbid") + Server.Count(uri, "tmdbid") + Server.Count(uri, "season") == 0), "No identifier or number is invented for a probe.");
        Assert.IsEmpty(server.Searches.Where(uri => Server.Param(uri, "t") == "movie"), "A function that advertises only identifiers is not asked at all.");
        Assert.AreEqual(IndexerCheckState.NotChecked, verification.Searches[IndexerSearchMode.Movie].State);
        Assert.IsFalse(verification.Rejected(IndexerSearchMode.Movie), "Not checked is neither proven nor broken.");
        StringAssert.Contains(verification.Searches[IndexerSearchMode.Movie].Message, "identifier");
    }

    [TestMethod]
    public async Task AFunctionThatRefusesEvenItsMinimalValidProbeIsMarkedRejectedAndTheWorkingIndexerStaysOn()
    {
        var server = new Server(uri => Server.Param(uri, "t") switch
        {
            "caps" => Xml(StructuredCaps),
            "tvsearch" => Xml(Error(200, "Missing parameter")),
            _ => Xml(Rss(Result))
        });
        using var world = new World(server);

        var result = await world.Setup.AddAsync("https://indexer.example", ApiKey, null, CancellationToken.None);

        var verification = result.Entry!.Settings.Verification!;
        Assert.IsTrue(verification.Rejected(IndexerSearchMode.TvSearch));
        Assert.IsFalse(verification.Answered(IndexerSearchMode.TvSearch));
        StringAssert.Contains(verification.Searches[IndexerSearchMode.TvSearch].Message, "t=tvsearch");
        Assert.IsTrue(verification.Answered(IndexerSearchMode.Search));
        Assert.IsTrue(result.Entry.Enabled, "A refused structured function never disables an indexer whose text search works.");
        Assert.AreEqual(IndexerReadinessLevel.Ready, IndexerReadiness.Level(result.Entry));
        Assert.AreEqual(IndexerCheckState.Valid, result.State);
    }

    [TestMethod]
    public async Task AnIndexerAddedTwiceIsRecognizedByItsAddress()
    {
        using var world = new World(CapsServer());
        await world.Setup.AddAsync("https://indexer.example", ApiKey, null, CancellationToken.None);

        var again = await world.Setup.AddAsync("https://INDEXER.example/api", "other", null, CancellationToken.None);

        Assert.AreEqual(IndexerSetupOutcome.Duplicate, again.Outcome);
        Assert.HasCount(1, await world.Store.LoadAllAsync());
    }

    [TestMethod]
    public async Task AnIndexerWhoseSearchIsRefusedIsStoredButStaysOffWithTheRefusedRequestNamed()
    {
        using var world = new World(CapsServer(_ => Xml(Error(200, "Missing parameter"))));

        var result = await world.Setup.AddAsync("https://indexer.example", ApiKey, null, CancellationToken.None);

        Assert.AreEqual(IndexerSetupOutcome.Added, result.Outcome);
        Assert.AreEqual(IndexerCheckState.ParametersRejected, result.State);
        StringAssert.Contains(result.Detail, "Missing parameter");
        StringAssert.Contains(result.Detail, "t=search");
        Assert.IsFalse(result.Entry!.Enabled, "A search that was never proven is not switched on.");
        Assert.AreEqual(IndexerReadinessLevel.NeedsAttention, IndexerReadiness.Level(result.Entry));
        Assert.AreEqual(2, world.Server.Searches.Count(uri => Server.Param(uri, "t") == "search"), "The bare feed, then once with a word; nothing more.");
    }

    [TestMethod]
    public async Task AValidSearchWithoutResultsIsAnsweredNotBroken()
    {
        using var world = new World(CapsServer(_ => Xml(Rss())));

        var result = await world.Setup.AddAsync("https://indexer.example", ApiKey, null, CancellationToken.None);

        Assert.AreEqual(IndexerCheckState.NoResults, result.State);
        Assert.IsTrue(result.Entry!.Enabled);
        Assert.AreEqual(IndexerReadinessLevel.Ready, IndexerReadiness.Level(result.Entry));
    }

    [TestMethod]
    public async Task TestSearchAsksOncePerDistinctCategorySetAndMarksTheTypesItProved()
    {
        using var world = new World(CapsServer());
        var added = await world.Setup.AddAsync("https://indexer.example", ApiKey, null, CancellationToken.None);
        world.Server.Requests.Clear();

        var result = await world.Setup.TestSearchAsync(added.Entry!.Id, CancellationToken.None);

        Assert.AreEqual(IndexerKindState.Verified, IndexerReadiness.ForKind(result.Entry!, MediaAcquisitionKind.Movie).State);
        Assert.AreEqual(IndexerKindState.Shared, IndexerReadiness.ForKind(result.Entry!, MediaAcquisitionKind.LightNovel).State, "A type that borrows the Books category stays marked as shared even after the search worked.");
        Assert.IsTrue(world.Server.Searches.Any(uri => Server.Param(uri, "cat") == "2000"), "Movies were asked in their own category.");
        var asked = world.Server.Searches.Where(uri => Server.Param(uri, "cat") is not null).Select(uri => Server.Param(uri, "cat")).ToArray();
        CollectionAssert.AreEqual(asked.Distinct().ToArray(), asked, "Types that resolve to the same categories share one request.");
    }

    [TestMethod]
    public void TheStandardTaxonomyMapsEveryMediaTypeAndNeverBorrowsAnotherOnesSection()
    {
        var map = IndexerCategoryMapper.Derive(Caps(StandardCaps)).ToDictionary(item => item.Kind);

        CollectionAssert.AreEqual(new[] { 2000 }, map[MediaAcquisitionKind.Movie].Categories);
        CollectionAssert.AreEqual(new[] { 5040 }, map[MediaAcquisitionKind.Tv].Categories, "TV excludes the Anime section the indexer lists beside it.");
        CollectionAssert.AreEqual(new[] { 5070 }, map[MediaAcquisitionKind.Anime].Categories);
        CollectionAssert.AreEqual(new[] { 3010, 3040 }, map[MediaAcquisitionKind.Music].Categories, "Music excludes audiobooks.");
        CollectionAssert.AreEqual(new[] { 3030 }, map[MediaAcquisitionKind.Audiobook].Categories);
        CollectionAssert.AreEqual(new[] { 7020 }, map[MediaAcquisitionKind.Book].Categories);
        CollectionAssert.AreEqual(new[] { 7030 }, map[MediaAcquisitionKind.Manga].Categories);
        Assert.AreEqual(IndexerCategoryEvidence.Shared, map[MediaAcquisitionKind.LightNovel].Evidence, "Light novels have no category of their own; they are marked as sharing the Books one.");
    }

    [TestMethod]
    public void CustomCategoriesAreMappedByTheirNamesAndUnrelatedOnesAreNotGuessed()
    {
        var map = IndexerCategoryMapper.Derive(Caps("""
            <caps><searching><search available="yes" supportedParams="q" /></searching>
            <categories>
              <category id="8000" name="Other"><subcat id="100301" name="Manga" /><subcat id="100302" name="Light Novels" /><subcat id="100303" name="Misc stuff" /></category>
              <category id="100400" name="Hörbücher Audiobooks" />
              <category id="2000" name="Movies" />
            </categories></caps>
            """)).ToDictionary(item => item.Kind);

        CollectionAssert.AreEqual(new[] { 100301 }, map[MediaAcquisitionKind.Manga].Categories);
        Assert.AreEqual(IndexerCategoryEvidence.Name, map[MediaAcquisitionKind.Manga].Evidence);
        CollectionAssert.AreEqual(new[] { 100302 }, map[MediaAcquisitionKind.LightNovel].Categories);
        CollectionAssert.AreEqual(new[] { 100400 }, map[MediaAcquisitionKind.Audiobook].Categories);
        Assert.IsFalse(map[MediaAcquisitionKind.Book].IsAvailable, "Nothing says Books here; the unknown category is not assigned to it.");
        Assert.IsFalse(map[MediaAcquisitionKind.Tv].IsAvailable);
        Assert.IsFalse(map.Values.Any(item => item.Categories.Contains(100303)), "An unnamed category belongs to no media type.");
    }

    [TestMethod]
    public void AnIndexerWhoseCapsListedOnlyNumbersStillMapsThroughTheStandardTaxonomy()
    {
        var map = IndexerCategoryMapper.Derive(new IndexerCapabilities(DateTimeOffset.UtcNow, new Dictionary<IndexerSearchMode, string[]> { [IndexerSearchMode.Search] = ["q"] }, null, [2000, 2040, 5000, 7000])).ToDictionary(item => item.Kind);

        CollectionAssert.AreEqual(new[] { 2000 }, map[MediaAcquisitionKind.Movie].Categories);
        CollectionAssert.AreEqual(new[] { 5000 }, map[MediaAcquisitionKind.Tv].Categories);
        CollectionAssert.AreEqual(new[] { 5070 }, map[MediaAcquisitionKind.Anime].Categories, "A section without listed sub categories covers the standard Anime one.");
        CollectionAssert.AreEqual(new[] { 7030 }, map[MediaAcquisitionKind.Manga].Categories);
        Assert.IsFalse(map[MediaAcquisitionKind.Music].IsAvailable);
    }

    [TestMethod]
    public async Task RefreshingKeepsTheOwnersSettingsAndAFailedRefreshKeepsTheWorkingConfiguration()
    {
        var offline = false;
        using var world = new World(new Server(uri => offline ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Server.Param(uri, "t") == "caps" ? Xml(StandardCaps) : Xml(Rss(Result))));
        var added = (await world.Setup.AddAsync("https://indexer.example", ApiKey, "Mine", CancellationToken.None)).Entry!;
        var edited = added with { Priority = 7, Settings = added.Settings with { SearchLimit = 40, AutomaticSearch = false, MediaKinds = [MediaAcquisitionKind.Manga], CategoriesByKind = new() { ["manga"] = [7099] } } };
        await world.Store.SaveAsync(edited);

        offline = true;
        var failed = await world.Setup.RefreshAsync(added.Id, CancellationToken.None);
        var afterFailure = (await world.Store.GetAsync(added.Id))!;

        Assert.AreEqual(IndexerSetupOutcome.ConnectionFailed, failed.Outcome);
        Assert.AreEqual(IndexerCheckState.Unavailable, failed.State);
        Assert.AreEqual(edited.Settings.Capabilities!.RefreshedAt, afterFailure.Settings.Capabilities!.RefreshedAt, "The earlier capabilities stay.");
        Assert.IsTrue(afterFailure.Settings.Verification!.Answered(IndexerSearchMode.Search), "The earlier proof stays.");
        Assert.IsNotNull(afterFailure.Settings.Verification.RefreshError);

        offline = false;
        var refreshed = await world.Setup.RefreshAsync(added.Id, CancellationToken.None);
        var after = refreshed.Entry!;

        Assert.AreEqual(IndexerSetupOutcome.Refreshed, refreshed.Outcome);
        Assert.AreEqual(7, after.Priority);
        Assert.AreEqual(40, after.Settings.SearchLimit);
        Assert.IsFalse(after.Settings.AutomaticSearch);
        CollectionAssert.AreEqual(new[] { MediaAcquisitionKind.Manga }, after.Settings.MediaKinds);
        CollectionAssert.AreEqual(new[] { 7099 }, IndexerCategoryMapper.Resolve(MediaAcquisitionKind.Manga, after).Categories.ToArray(), "The owner's category beats what was detected.");
        Assert.IsNull(after.Settings.Verification!.RefreshError);
        Assert.AreEqual(IndexerCategoryEvidence.Owner, IndexerCategoryMapper.Resolve(MediaAcquisitionKind.Manga, after).Evidence);
    }

    [TestMethod]
    public async Task SeveralCategoriesGoOutAsOneCommaSeparatedParameterAndAnExistingApiPathIsNotDoubled()
    {
        var server = new Server(_ => Xml(Rss(Result)));
        var indexer = new NewznabIndexer(new HttpClient(server), ProviderTestFactory.NewExecutor());

        var plain = Entry("A");
        await indexer.SearchAsync(plain with { Settings = plain.Settings with { Categories = [7030, 7020, 7030] } }, new IndexerSearchQuery("Berserk vol 1"), CancellationToken.None);
        var custom = Entry("B", "https://indexer.example/newznab");
        await indexer.SearchAsync(custom with { Settings = custom.Settings with { Categories = [] } }, new IndexerSearchQuery("Berserk"), CancellationToken.None);
        await indexer.SearchAsync(Entry("C", "https://indexer.example/api/"), new IndexerSearchQuery("Berserk"), CancellationToken.None);

        var first = server.Requests[0];
        Assert.AreEqual("/api", first.AbsolutePath);
        Assert.AreEqual(1, Server.Count(first, "cat"));
        Assert.AreEqual("7030,7020", Server.Param(first, "cat"));
        StringAssert.Contains(first.Query, "cat=7030%2C7020");
        Assert.AreEqual("search", Server.Param(first, "t"));
        Assert.AreEqual("Berserk vol 1", Server.Param(first, "q"));
        Assert.AreEqual("100", Server.Param(first, "limit"));
        Assert.AreEqual("/newznab/api", server.Requests[1].AbsolutePath);
        Assert.AreEqual(0, Server.Count(server.Requests[1], "cat"), "No category means no cat parameter at all.");
        Assert.AreEqual("/api", server.Requests[2].AbsolutePath);
    }

    [TestMethod]
    public async Task EmptyOrBlankParametersAreNeverSent()
    {
        var server = new Server(_ => Xml(Rss(Result)));
        var indexer = new NewznabIndexer(new HttpClient(server), ProviderTestFactory.NewExecutor());

        var nothing = await indexer.SearchAsync(Entry("A"), new IndexerSearchQuery("  ", IndexerSearchMode.TvSearch, [new("tvdbid", ""), new(" ", "x")]), CancellationToken.None);
        await indexer.SearchAsync(Entry("A"), new IndexerSearchQuery("Severance", IndexerSearchMode.TvSearch, [new("season", "1"), new("ep", " ")]), CancellationToken.None);

        Assert.IsEmpty(nothing);
        var sent = Assert.ContainsSingle(server.Requests);
        Assert.AreEqual("tvsearch", Server.Param(sent, "t"));
        Assert.AreEqual(0, Server.Count(sent, "ep"));
        Assert.AreEqual("1", Server.Param(sent, "season"));
    }

    [TestMethod]
    [DataRow(200, "Missing parameter", IndexerRejection.MissingParameter)]
    [DataRow(201, "Incorrect parameter", IndexerRejection.IncorrectParameter)]
    [DataRow(202, "No such function", IndexerRejection.UnsupportedFunction)]
    [DataRow(203, "Function not available", IndexerRejection.UnsupportedFunction)]
    public async Task ARefusedRequestShapeDeliveredAsHttp200NamesTheRequestWithoutTheKey(int code, string description, IndexerRejection expected)
    {
        var indexer = new NewznabIndexer(new HttpClient(new Server(_ => Xml(Error(code, description)))), ProviderTestFactory.NewExecutor());

        var exception = await Assert.ThrowsAsync<IndexerRequestRejectedException>(() => indexer.SearchAsync(Entry("Treasure Maps"), new IndexerSearchQuery("Berserk", IndexerSearchMode.Book, [new("author", "Miura")]), CancellationToken.None));

        Assert.AreEqual(expected, exception.Reason);
        Assert.AreEqual(IndexerSearchMode.Book, exception.Mode);
        StringAssert.Contains(exception.Message, description);
        StringAssert.Contains(exception.Message, "t=book");
        StringAssert.Contains(exception.Message, "author=Miura");
        Assert.IsFalse(exception.Message.Contains(ApiKey, StringComparison.Ordinal), "The API key is not in the message.");
    }

    [TestMethod]
    public async Task AccountProblemsLimitsAndBrokenAnswersAreDistinctFailures()
    {
        async Task<Exception> Failure(Func<Uri, HttpResponseMessage> respond) =>
            await Assert.ThrowsAsync<IndexerException>(() => new NewznabIndexer(new HttpClient(new Server(respond)), ProviderTestFactory.NewExecutor()).SearchAsync(Entry("X"), new IndexerSearchQuery("Berserk"), CancellationToken.None));

        Assert.IsInstanceOfType<IndexerAuthenticationException>(await Failure(_ => Xml(Error(100, "Incorrect user credentials"))));
        Assert.IsInstanceOfType<IndexerAuthenticationException>(await Failure(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.IsInstanceOfType<IndexerRateLimitedException>(await Failure(_ => Xml(Error(500, "Request limit reached"))));
        Assert.IsInstanceOfType<IndexerRateLimitedException>(await Failure(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var unknown = await Failure(_ => Xml(Error(910, "Account disabled by admin"), HttpStatusCode.BadRequest));
        Assert.IsNotInstanceOfType<IndexerAuthenticationException>(unknown);
        StringAssert.Contains(unknown.Message, "Account disabled by admin");
        StringAssert.Contains((await Failure(_ => Xml("<html><body>maintenance</body></html>"))).Message, "not Newznab XML");
        StringAssert.Contains((await Failure(_ => Xml("<rss><channel><item><title>x", HttpStatusCode.OK))).Message, "not Newznab XML");
        StringAssert.Contains((await Failure(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))).Message, "502");
    }

    [TestMethod]
    public async Task AValidAnswerWithoutResultsIsAnEmptyListNotAFailure()
    {
        var indexer = new NewznabIndexer(new HttpClient(new Server(_ => Xml(Rss()))), ProviderTestFactory.NewExecutor());

        Assert.IsEmpty(await indexer.SearchAsync(Entry("X"), new IndexerSearchQuery("Nothing like this"), CancellationToken.None));
    }

    [TestMethod]
    public async Task ARefusedStructuredSearchFallsBackToTextOnceAndIsNotRepeatedByTheNextSearch()
    {
        var server = CapsServer(uri => Server.Param(uri, "t") == "tvsearch" ? Xml(Error(200, "Missing parameter")) : Xml(Rss("Severance S01E02 1080p WEB-DL x264-GROUP")));
        using var world = new World(server);
        var entry = (await world.Setup.AddAsync("https://indexer.example", ApiKey, null, CancellationToken.None)).Entry!;
        await world.Store.SaveAsync(entry with { Settings = entry.Settings with { CategoriesByKind = null } });
        server.Requests.Clear();
        var intent = new SearchIntent(MediaAcquisitionKind.Tv, "Severance") { Season = 1, Episode = 2, ExternalIds = new Dictionary<string, string> { ["tvdb"] = "371980" } };

        var first = await world.Coordinator.SearchAsync(intent, new SearchOptions(), CancellationToken.None);
        var firstCalls = server.Searches.Select(uri => Server.Param(uri, "t")).ToArray();
        server.Requests.Clear();
        var second = await world.Coordinator.SearchAsync(intent, new SearchOptions(), CancellationToken.None);

        Assert.AreEqual(1, firstCalls.Count(function => function == "tvsearch"), "The refused request is sent once, never retried with the same shape.");
        Assert.IsTrue(firstCalls.Contains("search"), "The supported text search follows.");
        Assert.IsNotEmpty(first.Releases, "The text fallback found the release.");
        Assert.IsTrue(first.Outcomes.Single().Message!.Contains("Missing parameter", StringComparison.Ordinal) && first.Outcomes.Single().Message!.Contains("t=tvsearch", StringComparison.Ordinal), first.Outcomes.Single().Message);
        Assert.AreEqual(0, server.Searches.Count(uri => Server.Param(uri, "t") == "tvsearch"), "The next search skips the refused function.");
        Assert.IsNotEmpty(second.Releases);
    }

    [TestMethod]
    public async Task ATextSearchThatIsRefusedItselfIsReportedWithTheRequestAndStopsAfterOneAttemptPerQuery()
    {
        using var world = new World(CapsServer(_ => Xml(Error(200, "Missing parameter"))));
        var entry = Entry("Treasure Maps", capabilities: Caps(StandardCaps));
        await world.Store.SaveAsync(entry);

        var result = await world.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Manga, "Berserk") { Volume = 3 }, new SearchOptions(), CancellationToken.None);

        var outcome = result.Outcomes.Single();
        Assert.AreEqual(IndexerSearchState.ParametersRejected, outcome.State);
        StringAssert.Contains(outcome.Message, "t=search");
        StringAssert.Contains(outcome.Message, "cat=7030");
        Assert.IsFalse(outcome.Message!.Contains(ApiKey, StringComparison.Ordinal));
        Assert.AreEqual(1, world.Server.Searches.Count(), "A plain search that is refused is not repeated with other wording.");
        Assert.IsTrue(result.EveryIndexerFailed, "A refused search is a broken search, not a search without results.");
    }

    [TestMethod]
    public async Task AStrictServerThatAnswersMissingParameterToRepeatedCategoriesNowGetsOneCommaList()
    {
        // The shape of the Treasure Maps report: a Manga search on an indexer with Books/Comics categories. The server here refuses a repeated cat parameter; the
        // real indexer was not reachable, so this fixture only proves that Jularr no longer produces that request shape.
        var server = CapsServer(uri => Server.Count(uri, "cat") > 1 ? Xml(Error(200, "Missing parameter")) : Xml(Rss(Result)));
        using var world = new World(server);
        var caps = Caps(StandardCaps);
        await world.Store.SaveAsync(Entry("Treasure Maps", capabilities: caps, byKind: new() { ["manga"] = [7020, 7030] }));

        var result = await world.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Manga, "Berserk") { Volume = 1 }, new SearchOptions(), CancellationToken.None);

        Assert.IsNotEmpty(result.Releases);
        Assert.AreEqual(IndexerSearchState.Searched, result.Outcomes.Single().State);
        Assert.IsTrue(server.Searches.All(uri => Server.Count(uri, "cat") <= 1 && Server.Param(uri, "t") == "search"), "Manga uses the text search only; categories are one comma list.");
        Assert.AreEqual("7020,7030", Server.Param(server.Searches.First(), "cat"));
    }

    [TestMethod]
    public async Task MangaIsAskedInTheStandardCategoryOfOneIndexerAndTheCustomCategoryOfAnotherAndNeverInAnUnrelatedOne()
    {
        var server = new Server(_ => Xml(Rss(Result)));
        using var world = new World(server);
        var standard = Entry("Standard", "https://standard.example", Caps(StandardCaps), priority: 1);
        var custom = Entry("Custom", "https://custom.example", Caps("""<caps><searching><search available="yes" supportedParams="q"/></searching><categories><category id="100301" name="Manga &amp; Comics"/><category id="2000" name="Movies"/></categories></caps>"""), priority: 2);
        var moviesOnly = Entry("Movies only", "https://movies.example", Caps("""<caps><searching><search available="yes" supportedParams="q"/></searching><categories><category id="2000" name="Movies"><subcat id="2040" name="HD"/></category></categories></caps>"""), priority: 3);
        foreach (var entry in new[] { standard, custom, moviesOnly })
        {
            await world.Store.SaveAsync(entry);
        }

        var result = await world.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Manga, "Berserk") { Volume = 2 }, new SearchOptions(), CancellationToken.None);

        var byHost = server.Searches.ToLookup(uri => uri.Host);
        Assert.AreEqual("7030", Server.Param(byHost["standard.example"].First(), "cat"));
        Assert.AreEqual("100301", Server.Param(byHost["custom.example"].First(), "cat"));
        Assert.IsEmpty(byHost["movies.example"], "An indexer without a Manga category is not asked at all.");
        var skipped = result.Outcomes.Single(outcome => outcome.IndexerName == "Movies only");
        Assert.AreEqual(IndexerSearchState.Skipped, skipped.State);
        StringAssert.Contains(skipped.Message, "no category that belongs");
    }

    [TestMethod]
    public async Task OneBrokenIndexerCostsOnlyItsOwnShare()
    {
        var server = new Server(uri => uri.Host == "bad.example" ? Xml(Error(100, "Incorrect user credentials")) : Xml(Rss(Result)));
        using var world = new World(server);
        await world.Store.SaveAsync(Entry("Bad", "https://bad.example", Caps(StandardCaps), priority: 1));
        await world.Store.SaveAsync(Entry("Good", "https://good.example", Caps(StandardCaps), priority: 2));

        var result = await world.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021 }, new SearchOptions(), CancellationToken.None);

        Assert.IsNotEmpty(result.Releases, "The healthy indexer's results stay.");
        Assert.AreEqual(IndexerSearchState.AuthenticationFailed, result.Outcomes.Single(outcome => outcome.IndexerName == "Bad").State);
        Assert.AreEqual(IndexerSearchState.Searched, result.Outcomes.Single(outcome => outcome.IndexerName == "Good").State);
        Assert.IsFalse(result.EveryIndexerFailed);
    }

    [TestMethod]
    public async Task ARateLimitedIndexerIsLeftAloneInsteadOfBeingAskedAgain()
    {
        var server = new Server(_ => Xml(Error(500, "Request limit reached")));
        using var world = new World(server);
        await world.Store.SaveAsync(Entry("Limited", capabilities: Caps(StandardCaps)));
        var intent = new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021 };

        var first = await world.Coordinator.SearchAsync(intent, new SearchOptions(), CancellationToken.None);
        var calls = server.Requests.Count;
        var second = await world.Coordinator.SearchAsync(intent, new SearchOptions(), CancellationToken.None);

        Assert.AreEqual(IndexerSearchState.RateLimited, first.Outcomes.Single().State);
        Assert.AreEqual(IndexerSearchState.RateLimited, second.Outcomes.Single().State);
        Assert.AreEqual(calls, server.Requests.Count, "The second search does not call a rate-limited indexer.");
    }

    [TestMethod]
    public async Task AutomaticAndManualSearchSendTheSameRequests()
    {
        var automaticServer = CapsServer();
        var manualServer = CapsServer();
        var intent = new SearchIntent(MediaAcquisitionKind.Tv, "Severance") { Season = 1, Episode = 2, ExternalIds = new Dictionary<string, string> { ["tvdb"] = "371980" } };
        async Task<string[]> Requests(Server server, SearchOptions options)
        {
            using var world = new World(server);
            var entry = (await world.Setup.AddAsync("https://indexer.example", ApiKey, null, CancellationToken.None)).Entry!;
            server.Requests.Clear();
            await world.Coordinator.SearchAsync(intent, options, CancellationToken.None);
            return [.. server.Searches.Select(uri => uri.Query)];
        }

        var automatic = await Requests(automaticServer, new SearchOptions { Purpose = SearchPurpose.Automatic });
        var manual = await Requests(manualServer, new SearchOptions { Purpose = SearchPurpose.Interactive, Refresh = true });

        Assert.IsNotEmpty(automatic);
        CollectionAssert.AreEqual(automatic, manual, "One planner and one executor serve both.");
        Assert.IsTrue(automatic[0].Contains("tvdbid=371980", StringComparison.Ordinal) && automatic[0].Contains("season=1", StringComparison.Ordinal) && automatic[0].Contains("ep=2", StringComparison.Ordinal), "The strongest advertised structured search comes first.");
        Assert.IsTrue(automatic.All(query => !query.Contains("imdbid", StringComparison.Ordinal) && !query.Contains("tmdbid", StringComparison.Ordinal)), "A parameter the indexer did not advertise is never sent.");
    }

    [TestMethod]
    public async Task ExistingIndexerConfigurationWithoutVerificationKeepsWorkingAndIsReadAsNotChecked()
    {
        using var world = new World(CapsServer());
        var legacy = Entry("Legacy", capabilities: null);
        await world.Store.SaveAsync(legacy);

        var result = await world.Coordinator.SearchAsync(new SearchIntent(MediaAcquisitionKind.Movie, "Dune") { Year = 2021 }, new SearchOptions(), CancellationToken.None);

        Assert.IsNotEmpty(result.Releases);
        Assert.AreEqual(IndexerReadinessLevel.NotChecked, IndexerReadiness.Level(legacy));
        CollectionAssert.AreEqual(new[] { 2000 }, IndexerCategoryMapper.Resolve(MediaAcquisitionKind.Movie, legacy).Categories);
        Assert.AreEqual(IndexerCategoryEvidence.Assumed, IndexerCategoryMapper.Resolve(MediaAcquisitionKind.Movie, legacy).Evidence);
    }

    [TestMethod]
    public async Task TheOwnerAddsAnIndexerWithOnlyAddressAndKeyAndTheUsenetPageShowsWhatWasDetected()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", "Dune.2021.1080p.WEB-DL.x264-GROUP");
        var server = CapsServer();
        await using var host = await VideoAdminPageHost.CreateAsync(video, server);

        var status = await host.PostAsync("/Settings/Indexers/Edit", "/Settings/Indexers/Edit", [new("BaseUrl", "indexer.example"), new("ApiKey", ApiKey), new("Priority", "1"), new("SearchLimit", "100"), new("Enabled", "true"), new("AutomaticSearch", "true"), new("InteractiveSearch", "true")]);
        var html = await host.GetHtmlAsync("/Admin/Usenet");

        Assert.AreEqual(HttpStatusCode.Found, status);
        StringAssert.Contains(html, "Treasure Maps");
        StringAssert.Contains(html, ">Ready<");
        StringAssert.Contains(html, "Test search");
        StringAssert.Contains(html, "Refresh capabilities");
        StringAssert.Contains(html, "Not searched yet", "Categories were detected, but no search in them has run.");
        StringAssert.Contains(html, "Structured searches");
        StringAssert.Contains(html, "TV: works");
        Assert.IsFalse(html.Contains(ApiKey, StringComparison.Ordinal), "The key is never rendered.");
        var stored = (await host.Video.Get<IndexerStore>().LoadAllAsync()).Single(item => item.Settings.BaseUrl == "https://indexer.example");
        Assert.AreEqual("https://indexer.example", stored.Settings.BaseUrl);
        Assert.IsNull(stored.Settings.CategoriesByKind, "No category was typed, none is stored.");
    }

    [TestMethod]
    public async Task ARejectedKeyKeepsTheFormOpenExplainsTheCauseAndStoresNothing()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", "Dune.2021.1080p.WEB-DL.x264-GROUP");
        await using var host = await VideoAdminPageHost.CreateAsync(video, new Server(_ => Xml(Error(100, "Incorrect user credentials"))));

        var html = await host.PostHtmlAsync("/Settings/Indexers/Edit", "/Settings/Indexers/Edit", [new("BaseUrl", "https://indexer.example"), new("ApiKey", ApiKey)]);

        StringAssert.Contains(html, "rejected the API key or account");
        StringAssert.Contains(html, "value=\"https://indexer.example\"", "What was typed stays in the form.");
        Assert.IsFalse(html.Contains(ApiKey, StringComparison.Ordinal));
        Assert.IsFalse((await host.Video.Get<IndexerStore>().LoadAllAsync()).Any(item => item.Settings.BaseUrl == "https://indexer.example"), "Nothing was stored for the rejected address.");
    }

    [TestMethod]
    public async Task TestSearchRefreshAndTheAdvancedOverridesWorkFromTheUsenetPageAndTheEditForm()
    {
        await using var video = await VideoAcquisitionTestHost.CreateAsync(MediaAcquisitionKind.Movie, "Dune", 2021, "438631", "Dune.2021.1080p.WEB-DL.x264-GROUP");
        var server = CapsServer();
        await using var host = await VideoAdminPageHost.CreateAsync(video, server);
        await host.PostAsync("/Settings/Indexers/Edit", "/Settings/Indexers/Edit", [new("BaseUrl", "https://indexer.example"), new("ApiKey", ApiKey)]);
        var entry = (await host.Video.Get<IndexerStore>().LoadAllAsync()).Single(item => item.Settings.BaseUrl == "https://indexer.example");

        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync("/Admin/Usenet", $"/Admin/Usenet?handler=TestIndexerSearch&id={entry.Id}", []));
        var afterSearch = await host.GetHtmlAsync("/Admin/Usenet");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync("/Admin/Usenet", $"/Admin/Usenet?handler=RefreshIndexer&id={entry.Id}", []));

        StringAssert.Contains(afterSearch, "Search checked");
        var form = await host.GetHtmlAsync($"/Settings/Indexers/Edit/{entry.Id}");
        StringAssert.Contains(form, "placeholder=\"Detected: 2000\"", "The override field shows what was detected.");
        Assert.AreEqual(HttpStatusCode.Found, await host.PostAsync($"/Settings/Indexers/Edit/{entry.Id}", $"/Settings/Indexers/Edit/{entry.Id}", [new("BaseUrl", "https://indexer.example"), new("Name", "Mine"), new("Priority", "4"), new("SearchLimit", "50"), new("Enabled", "true"), new("AutomaticSearch", "true"), new("InteractiveSearch", "false"), new("KindOverrides[manga]", "7099, 7030"), new("SearchedKinds", "manga"), new("SearchedKinds", "movie")]));

        var saved = (await host.Video.Get<IndexerStore>().LoadAllAsync()).Single(item => item.Settings.BaseUrl == "https://indexer.example");
        Assert.AreEqual("Mine", saved.Name);
        Assert.AreEqual(4, saved.Priority);
        Assert.IsFalse(saved.Settings.InteractiveSearch, "An unchecked box is off.");
        CollectionAssert.AreEqual(new[] { 7030, 7099 }, saved.Settings.CategoriesFor(MediaAcquisitionKind.Manga));
        CollectionAssert.AreEquivalent(new[] { MediaAcquisitionKind.Manga, MediaAcquisitionKind.Movie }, saved.Settings.MediaKinds);
        Assert.IsNotNull(saved.Settings.Capabilities, "Saving the form keeps what was detected.");
        Assert.IsNotNull(saved.Settings.Verification);
    }
}
