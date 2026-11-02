using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jularr.E2E.Tests
{
    // A release the fake indexer offers and the file, or folder of files, its finished download contains.
    internal sealed record FakeRelease(string Title, string Guid, int Category, string File)
    {
        // The size the indexer states: a book or comic volume is a few MB, anything else keeps the old 400 MB, so a book stays inside the size limit per volume.
        public long Length => Category is >= 7000 and < 8000 ? 4_000_000 : 400_000_000;
    }

    // TMDB, a Newznab indexer and SABnzbd on loopback, so the separately started Jularr never needs a live service.
    internal sealed class FakeServices : IAsyncDisposable
    {
        public const string TmdbToken = "eyJ-e2e-read-token";
        public const string TvdbKey = "e2e-tvdb-project-key";
        public const string IndexerKey = "e2e-indexer-key";
        public const string ClientKey = "e2e-client-key";
        public const string MovieTitle = "Big Buck Bunny";
        public const string SeriesTitle = "Jularr Test Series";
        public const string AlbumTitle = "Example Album";
        public const string BookTitle = "Pride and Prejudice";
        public const string MangaTitle = "Example Manga";
        public const string NovelTitle = "Example Novel";
        public const string AnimeTitle = "Example Anime";
        public const string SonarrKey = "e2e-sonarr-key";
        private const string AniListAnime = """{"id":103,"type":"ANIME","format":"TV","title":{"romaji":"Reidai Anime","english":"Example Anime","native":null},"synonyms":[],"startDate":{"year":2021},"episodes":2,"status":"FINISHED","description":"A test anime.","coverImage":null,"relations":{"edges":[]},"staff":{"edges":[]}}""";
        private const string AnimeSeries = """{"id":99002,"name":"Example Anime","original_name":"Reidai Anime","first_air_date":"2021-01-05","overview":"Two test episodes.","original_language":"ja","origin_country":["JP"],"genres":[{"id":16,"name":"Animation"}],"external_ids":{"tvdb_id":990002},"seasons":[{"season_number":1,"name":"Season 1","air_date":"2021-01-05","episode_count":2}]}""";
        private const string AniListNovel = """{"id":102,"type":"MANGA","format":"NOVEL","title":{"romaji":"Reidai Shousetsu","english":"Example Novel","native":null},"synonyms":[],"startDate":{"year":2020},"volumes":1,"status":"FINISHED","description":"A test light novel.","coverImage":null,"staff":{"edges":[{"role":"Story","node":{"name":{"full":"Ren Example"}}}]}}""";
        private const string AniListManga = """{"id":101,"type":"MANGA","format":"MANGA","title":{"romaji":"Reidai Manga","english":"Example Manga","native":null},"synonyms":[],"startDate":{"year":2019},"volumes":1,"status":"FINISHED","description":"A test series.","coverImage":null,"staff":{"edges":[{"role":"Story & Art","node":{"name":{"full":"Mika Example"}}}]}}""";
        private const string AlbumId = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b";
        private const string Album = $$"""{"id":"{{AlbumId}}","title":"Example Album","primary-type":"Album","first-release-date":"2019-05-03","artist-credit":[{"name":"Example Artist","joinphrase":""}]}""";

        private const string Movie = """{"id":10378,"title":"Big Buck Bunny","original_title":"Big Buck Bunny","release_date":"2008-05-30","overview":"A giant rabbit takes revenge on three bullies.","original_language":"en","genres":[{"id":16,"name":"Animation"}],"external_ids":{"imdb_id":"tt1254207"}}""";
        private const string Series = """{"id":99001,"name":"Jularr Test Series","original_name":"Jularr Test Series","first_air_date":"2020-04-01","poster_path":"/series.png","backdrop_path":"/series-backdrop.png","overview":"Two test episodes.","original_language":"en","origin_country":["DE"],"external_ids":{"tvdb_id":990001},"seasons":[{"season_number":1,"name":"Season 1","air_date":"2020-04-01","episode_count":2}]}""";

        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _grabbed = new();
        private readonly ConcurrentDictionary<string, FakeRelease> _completed = new();
        private readonly ConcurrentDictionary<string, FakeRelease> _held = new();
        private volatile FakeRelease[] _offered = [];

        private FakeServices(WebApplication app, Uri address, string downloads)
        {
            _app = app;
            Address = address;
            Downloads = downloads;
        }

        public Uri Address { get; }

        public Uri Tmdb3 => new(Address, "tmdb/3/");

        // TheTVDB names the seasons and episodes of every series.
        public Uri Tvdb4 => new(Address, "tvdb/v4/");

        // The provider's image host: TMDB's posters and backdrops are served from here and counted.
        public Uri Images => new(Address, "images/");

        public int ImageRequests => _imageRequests;
        public byte[] ImagePayload { get; set; } = Pixel;

        private int _imageRequests;

        // A one-pixel PNG every image address answers with.
        private static readonly byte[] Pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        public Uri MusicBrainz => new(Address, "musicbrainz/ws/2/");

        public Uri OpenLibrary => new(Address, "openlibrary/");

        public Uri AniList => new(Address, "anilist/");

        // A person's own AniList: the sign-in (authorization code to token), their lists and the progress they receive.
        public Uri AniListAccount => new(Address, "anilist-account/");

        public const string AniListClientId = "9876";
        public const string AniListSecret = "e2e-anilist-secret";
        public const string AniListCode = "e2e-anilist-code";
        public const string AniListToken = "e2e-anilist-access-token-0123456789";

        // The progress of each list entry (id, progress) as AniList has it; "writes" are the ones Jularr sent.
        private readonly ConcurrentDictionary<int, int> _aniListProgress = new() { [8801] = 1, [8802] = 3 };
        private readonly ConcurrentQueue<string> _aniListWrites = new();
        private long _aniListStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;

        public IReadOnlyDictionary<int, int> AniListProgress => _aniListProgress;
        public IReadOnlyCollection<string> AniListWrites => _aniListWrites;

        // A Sonarr with one quality profile, its custom format, a root folder and the test series missing.
        public Uri Sonarr => new(Address, "sonarr/");

        // Where SABnzbd puts finished jobs; Jularr maps "/downloads" here.
        public string Downloads { get; }

        public IReadOnlyCollection<string> Grabbed => _grabbed;

        // What a person told SABnzbd: "queue:delete:id".
        private readonly ConcurrentQueue<string> _commands = new();
        public IReadOnlyCollection<string> Commands => _commands;

        private volatile bool _holdDownloads;
        private volatile bool _failDownloads;
        private volatile int _heldPercent = 40;

        // Grabbed jobs wait in the queue until finished.
        public bool HoldDownloads { get => _holdDownloads; set => _holdDownloads = value; }

        // How far held jobs say they are, of a size of 1000 MB.
        public int HeldPercent { get => _heldPercent; set => _heldPercent = value; }

        // Finished jobs report a broken download.
        public bool FailDownloads { get => _failDownloads; set => _failDownloads = value; }

        public void FinishDownloads()
        {
            foreach (var guid in _held.Keys)
            {
                if (_held.TryRemove(guid, out var release))
                {
                    Complete(release);
                }
            }
        }

        public static async Task<FakeServices> StartAsync(string downloads)
        {
            var port = FreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:" + port);
            var app = builder.Build();
            var services = new FakeServices(app, new Uri("http://127.0.0.1:" + port + "/"), downloads);

            // Every call joins the evidence, so a failure shows what Jularr asked for.
            app.Use(async (http, next) =>
            {
                try
                {
                    await next(http);
                    Evidence.Log("[fake] " + http.Request.Method + " " + http.Request.Path + http.Request.QueryString + " " + http.Response.StatusCode);
                }
                catch (Exception exception)
                {
                    Evidence.Log("[fake] " + http.Request.Method + " " + http.Request.Path + " failed: " + exception);
                    throw;
                }
            });
            app.MapGet("/tmdb/3/{**path}", Tmdb);
            app.MapMethods("/tvdb/v4/{**path}", ["GET", "POST"], (HttpRequest request, string path) => Tvdb(request, path));
            app.MapGet("/images/{**path}", () =>
            {
                Interlocked.Increment(ref services._imageRequests);
                return Results.Bytes(services.ImagePayload, "image/png");
            });
            app.MapGet("/musicbrainz/ws/2/{**path}", MusicBrainzAnswer);
            app.MapGet("/openlibrary/{**path}", OpenLibraryAnswer);
            app.MapPost("/anilist/", services.AniListGraphQl);
            app.MapPost("/anilist-account/api/v2/oauth/token", services.AniListSignIn);
            app.MapGet("/sonarr/api/v3/{**path}", SonarrAnswer);
            app.MapGet("/newznab/api", services.Newznab);
            app.MapGet("/newznab/getnzb/{id}", (string id) => Results.Text("<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"></nzb>", "application/x-nzb"));
            app.MapPost("/sabnzbd/api", services.Sabnzbd);
            await app.StartAsync();
            return services;
        }

        // Releases appear only now, just before their manual search, so no automatic search grabs first.
        public void Offer(params FakeRelease[] releases) => _offered = [.. _offered, .. releases];

        public Uri NzbAddress(FakeRelease release) => new(Address, "newznab/getnzb/" + release.Guid);

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        private static IResult Tmdb(HttpContext http, string path)
        {
            if (http.Request.Headers.Authorization.ToString() != "Bearer " + TmdbToken)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var query = http.Request.Query["query"].ToString();
            return path switch
            {
                "search/movie" => Json(query.Contains("Bunny", StringComparison.OrdinalIgnoreCase) ? "{\"results\":[" + Movie + "]}" : "{\"results\":[]}"),
                "search/tv" => Json(query.Contains("Test Series", StringComparison.OrdinalIgnoreCase) ? "{\"results\":[" + Series + "]}"
                    : query.Contains("Anime", StringComparison.OrdinalIgnoreCase) ? "{\"results\":[" + AnimeSeries + "]}" : "{\"results\":[]}"),
                "tv/99002" => Json(AnimeSeries),
                "tv/99002/season/1" => Json("""{"episodes":[{"season_number":1,"episode_number":1,"name":"Beginning","air_date":"2021-01-05"},{"season_number":1,"episode_number":2,"name":"Ending","air_date":"2021-01-12"}]}"""),
                "movie/10378" => Json(Movie),

                // Deterministic provider fixtures for large imports and restart recovery.
                _ when path.StartsWith("movie/3", StringComparison.Ordinal) && int.TryParse(path.AsSpan(6), out var bulk) && bulk is >= 30000 and < 30400 =>
                    Json("{\"id\":" + bulk + ",\"title\":\"Bulk Movie " + bulk + "\",\"original_title\":\"Bulk Movie " + bulk + "\",\"release_date\":\"2010-01-01\",\"overview\":\"A film of the bulk list.\",\"original_language\":\"en\","
                        + "\"genres\":[{\"id\":16,\"name\":\"Animation\"}],\"external_ids\":{\"imdb_id\":\"tt" + bulk.ToString("D7", System.Globalization.CultureInfo.InvariantCulture) + "\"}}"),
                "tv/99001" => Json(Series),
                "tv/99001/season/1" => Json("""{"episodes":[{"season_number":1,"episode_number":1,"name":"Pilot","air_date":"2020-04-01"},{"season_number":1,"episode_number":2,"name":"Second","air_date":"2020-04-08"}]}"""),
                _ when path.StartsWith("trending/", StringComparison.Ordinal) || path.StartsWith("discover/", StringComparison.Ordinal) || path.EndsWith("/top_rated", StringComparison.Ordinal)
                    || path == "movie/upcoming" => Json(path.Contains("movie", StringComparison.Ordinal) ? "{\"results\":[" + Movie + "]}" : "{\"results\":[" + Series + "]}"),
                _ => Results.NotFound()
            };
        }

        private static IResult Tvdb(HttpRequest request, string path) => path switch
        {
            "login" => Json("""{"data":{"token":"e2e-tvdb-token"}}"""),
            // Series are searched at TheTVDB.
            "search" when request.Query["query"].ToString().Contains("Test Series", StringComparison.OrdinalIgnoreCase) => Json("""
                {"data":[{"tvdb_id":"990001","name":"Jularr Test Series","year":"2020","type":"series","primary_language":"eng","image_url":"https://artworks.thetvdb.com/banners/series.png",
                "translations":{"eng":"Jularr Test Series"},"overviews":{"eng":"Two test episodes."}}]}
                """),
            "search" when request.Query["query"].ToString().Contains("Anime", StringComparison.OrdinalIgnoreCase) => Json("""
                {"data":[{"tvdb_id":"990002","name":"Reidai Anime","year":"2021","type":"series","primary_language":"jpn","translations":{"jpn":"Reidai Anime","eng":"Example Anime"},
                "overviews":{"eng":"Two test episodes."}}]}
                """),
            "search" => Json("""{"data":[]}"""),
            // Series and anime come from TheTVDB with all their facts; TMDB only names them in its lists and gives the rating.
            "series/990001/extended" => Json("""
                {"data":{"id":990001,"name":"Jularr Test Series","overview":"Two test episodes.","firstAired":"2020-04-01","year":"2020","originalLanguage":"eng","originalCountry":"deu",
                "status":{"name":"Ended"},"remoteIds":[{"id":"99001","sourceName":"TheMovieDB.com"},{"id":"tt9900001","sourceName":"IMDB"}],"genres":[{"name":"Drama"}],"originalNetwork":{"name":"Test Network"},
                "artworks":[{"image":"https://artworks.thetvdb.com/banners/series.png","language":"eng","type":2,"score":10},{"image":"https://artworks.thetvdb.com/banners/series-backdrop.png","type":3,"score":10}],
                "trailers":[{"url":"https://www.youtube.com/watch?v=aaaaaaaaaaa","language":"eng"}],
                "characters":[{"name":"Lead","peopleId":9001,"peopleType":"Actor","personName":"Test Actor","sort":1}],
                "seasons":[{"number":1,"type":{"type":"official"}}],
                "translations":{"nameTranslations":[{"name":"Jularr Test Series","language":"eng"}],"overviewTranslations":[{"overview":"Two test episodes.","language":"eng"}]}}}
                """),
            "series/990001/episodes/default" => Json("""{"data":{"episodes":[{"id":1,"seasonNumber":1,"number":1,"name":"Pilot","aired":"2020-04-01","overview":"The first test episode."},{"id":2,"seasonNumber":1,"number":2,"name":"Second","aired":"2020-04-08"}]},"links":{"next":null}}"""),
            "series/990001/episodes/default/eng" => Json("""{"data":{"episodes":[{"id":1,"seasonNumber":1,"number":1,"name":"Pilot","aired":"2020-04-01","overview":"The first test episode."},{"id":2,"seasonNumber":1,"number":2,"name":"Second","aired":"2020-04-08"}]},"links":{"next":null}}"""),
            "series/990002/extended" => Json("""
                {"data":{"id":990002,"name":"Reidai Anime","overview":"Futatsu no tesuto.","firstAired":"2021-01-05","year":"2021","originalLanguage":"jpn","originalCountry":"jpn",
                "remoteIds":[{"id":"99002","sourceName":"TheMovieDB.com"}],"genres":[{"name":"Animation"},{"name":"Anime"}],"seasons":[{"number":1,"type":{"type":"official"}}],
                "translations":{"nameTranslations":[{"name":"Reidai Anime","language":"jpn"},{"name":"Example Anime","language":"eng"}],"overviewTranslations":[{"overview":"Two test episodes.","language":"eng"}]}}}
                """),
            "series/990002/episodes/default" => Json("""{"data":{"episodes":[{"id":3,"seasonNumber":1,"number":1,"name":"Hajimari","aired":"2021-01-05"},{"id":4,"seasonNumber":1,"number":2,"name":"Owari","aired":"2021-01-12"}]},"links":{"next":null}}"""),
            "series/990002/episodes/default/eng" => Json("""{"data":{"episodes":[{"id":3,"seasonNumber":1,"number":1,"name":"Beginning","aired":"2021-01-05"},{"id":4,"seasonNumber":1,"number":2,"name":"Ending","aired":"2021-01-12"}]},"links":{"next":null}}"""),
            _ => Results.NotFound()
        };

        // One album on two discs: two tracks, then one.
        private static IResult MusicBrainzAnswer(string path) => path switch
        {
            "release-group" => Json("{\"release-groups\":[" + Album + "]}"),
            "release-group/" + AlbumId => Json(Album),
            "release" => Json("""{"releases":[{"id":"aaaaaaaa-0000-0000-0000-000000000001","date":"2019-05-03"}]}"""),
            "release/aaaaaaaa-0000-0000-0000-000000000001" => Json("""{"media":[{"position":1,"tracks":[{"title":"Opening"},{"title":"Middle Song"}]},{"position":2,"tracks":[{"title":"Closing"}]}]}"""),
            _ => Results.NotFound()
        };

        // A classic novel and a one-volume manga series.
        private static IResult OpenLibraryAnswer(HttpRequest request, string path) => path switch
        {
            "search.json" when request.Query["q"].ToString().Contains("Manga", StringComparison.OrdinalIgnoreCase) || request.Query["title"].ToString().Contains("Manga", StringComparison.OrdinalIgnoreCase)
                => Json("""{"docs":[{"key":"/works/OL9W","title":"Example Manga","author_name":["Mika Example"],"first_publish_year":2019}]}"""),
            "search.json" => Json("""{"docs":[{"key":"/works/OL66554W","title":"Pride and Prejudice","author_name":["Jane Austen"],"first_publish_year":1813}]}"""),
            "trending/weekly.json" => Json("""{"works":[{"key":"/works/OL66554W","title":"Pride and Prejudice","author_name":["Jane Austen"],"first_publish_year":1813}]}"""),
            "works/OL66554W.json" => Json("""{"title":"Pride and Prejudice","first_publish_date":"1813","description":{"value":"A novel of manners."},"authors":[{"author":{"key":"/authors/OL21594A"}}]}"""),
            "works/OL9W.json" => Json("""{"title":"Example Manga","first_publish_date":"2019","authors":[{"author":{"key":"/authors/OL1A"}}]}"""),

            // Without a language the edition is only compared, never saved as a catalog edition on import.
            "works/OL66554W/editions.json" => Json("""{"entries":[{"key":"/books/OL7353617M","title":"Pride and Prejudice","physical_format":"Paperback","isbn_13":["9780141439518"],"publishers":["Penguin Classics"],"publish_date":"2003"}]}"""),
            "authors/OL21594A.json" => Json("""{"name":"Jane Austen"}"""),
            "authors/OL1A.json" => Json("""{"name":"Mika Example"}"""),
            _ => Results.NotFound()
        };

        // The code of AniList's PIN page becomes the person's token.
        private async Task<IResult> AniListSignIn(HttpRequest request)
        {
            using var body = System.Text.Json.JsonDocument.Parse(await new StreamReader(request.Body).ReadToEndAsync());
            var root = body.RootElement;
            var valid = root.GetProperty("client_id").GetString() == AniListClientId && root.GetProperty("client_secret").GetString() == AniListSecret
                && root.GetProperty("code").GetString() == AniListCode && root.GetProperty("grant_type").GetString() == "authorization_code";
            return valid ? Json("{\"token_type\":\"Bearer\",\"expires_in\":31536000,\"access_token\":\"" + AniListToken + "\"}") : Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        // A person's calls carry their token and see their own lists; everything else is the public catalog.
        private async Task<IResult> AniListGraphQl(HttpRequest request)
        {
            var text = await new StreamReader(request.Body).ReadToEndAsync();
            if (request.Headers.Authorization.ToString() != "Bearer " + AniListToken)
            {
                return request.Headers.Authorization.Count > 0 ? Results.StatusCode(StatusCodes.Status401Unauthorized) : AniListAnswer(text);
            }

            using var body = System.Text.Json.JsonDocument.Parse(text);
            var query = body.RootElement.GetProperty("query").GetString()!;
            var variables = body.RootElement.GetProperty("variables");
            if (query.Contains("Viewer", StringComparison.Ordinal))
            {
                return Json("""{"data":{"Viewer":{"id":4711,"name":"E2E Fan"}}}""");
            }

            if (query.Contains("MediaListCollection", StringComparison.Ordinal))
            {
                if (variables.GetProperty("type").GetString() != "ANIME")
                {
                    return Json("""{"data":{"MediaListCollection":{"hasNextChunk":false,"lists":[]}}}""");
                }

                string Entry(int id, int mediaId, string status, int episodes) => "{\"id\":" + id + ",\"mediaId\":" + mediaId + ",\"status\":\"" + status + "\",\"progress\":" + _aniListProgress[id]
                    + ",\"progressVolumes\":0,\"score\":85,\"updatedAt\":" + Interlocked.Read(ref _aniListStamp) + ",\"media\":{\"episodes\":" + episodes + ",\"chapters\":null,\"volumes\":null,\"title\":{\"romaji\":\"Entry " + mediaId + "\",\"english\":null}}}";
                return Json("{\"data\":{\"MediaListCollection\":{\"hasNextChunk\":false,\"lists\":[{\"entries\":[" + Entry(8801, 7001, "CURRENT", 12) + "," + Entry(8802, 7999, "CURRENT", 24) + "]}]}}}");
            }

            if (query.Contains("SaveMediaListEntry", StringComparison.Ordinal))
            {
                var id = variables.GetProperty("id").GetInt32();
                var progress = variables.GetProperty("progress").GetInt32();
                _aniListProgress[id] = progress;
                _aniListWrites.Enqueue(id + ":" + progress);
                var stamp = Interlocked.Increment(ref _aniListStamp);
                return Json("{\"data\":{\"SaveMediaListEntry\":{\"id\":" + id + ",\"progress\":" + progress + ",\"progressVolumes\":0,\"updatedAt\":" + stamp + "}}}");
            }

            return Results.NotFound();
        }

        private static IResult AniListAnswer(string query)
        {
            var media = query.Contains("ANIME", StringComparison.Ordinal) || query.Contains("103", StringComparison.Ordinal) ? AniListAnime
                : query.Contains("Novel", StringComparison.Ordinal) || query.Contains("102", StringComparison.Ordinal) ? AniListNovel : AniListManga;
            return Json(query.Contains("Page(", StringComparison.Ordinal) ? "{\"data\":{\"Page\":{\"media\":[" + media + "]}}}" : "{\"data\":{\"Media\":" + media + "}}");
        }

        // Sonarr's API v3 as a takeover reads it; indexers and clients are set up in Jularr itself.
        private static IResult SonarrAnswer(HttpContext http, string path)
        {
            if (http.Request.Headers["X-Api-Key"] != SonarrKey)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            return path switch
            {
                "indexer" or "downloadclient" => Json("[]"),
                "rootfolder" => Json("""[{"path":"/tv","defaultQualityProfileId":4}]"""),
                "series" => Json("""[{"title":"Jularr Test Series","year":2020,"tvdbId":990001,"tmdbId":99001,"imdbId":"tt9900001","monitored":true,"qualityProfileId":4,"path":"/tv/Jularr Test Series","statistics":{"episodeFileCount":0,"episodeCount":2}}]"""),
                "qualityprofile" => Json("""
                    [{"id":4,"name":"Web 1080p","upgradeAllowed":false,"cutoff":1002,"minFormatScore":0,"items":[
                      {"quality":{"id":4,"name":"HDTV-720p","source":"television","resolution":720},"items":[],"allowed":false},
                      {"name":"WEB 1080p","id":1002,"allowed":true,"items":[
                        {"quality":{"id":3,"name":"WEBDL-1080p","source":"web","resolution":1080},"items":[],"allowed":true},
                        {"quality":{"id":15,"name":"WEBRip-1080p","source":"webRip","resolution":1080},"items":[],"allowed":true}]}],
                     "formatItems":[{"format":1,"name":"x265","score":-10000}]}]
                    """),
                "customformat" => Json("""[{"id":1,"name":"x265","specifications":[{"implementation":"ReleaseTitleSpecification","negate":false,"required":true,"fields":[{"name":"value","value":"[xh]\\.?265"}]}]}]"""),
                "language" or "indexerflag" or "customformat/schema" => Json("[]"),
                _ => Results.NotFound()
            };
        }

        private IResult Newznab(HttpContext http)
        {
            if (http.Request.Query["apikey"] != IndexerKey)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (http.Request.Query["t"] == "caps")
            {
                return Results.Text("""
                    <caps>
                      <server title="E2E" version="1.0" />
                      <limits max="100" default="100" />
                      <searching>
                        <search available="yes" supportedParams="q" />
                        <movie-search available="yes" supportedParams="q" />
                        <book-search available="yes" supportedParams="q,author,title" />
                        <tv-search available="yes" supportedParams="q,season,ep" />
                      </searching>
                      <categories>
                        <category id="2000" name="Movies"><subcat id="2040" name="HD" /></category>
                        <category id="5000" name="TV"><subcat id="5040" name="HD" /><subcat id="5070" name="Anime" /></category>
                        <category id="3000" name="Audio"><subcat id="3040" name="Lossless" /></category>
                        <category id="7000" name="Books"><subcat id="7020" name="EBook" /><subcat id="7030" name="Comics" /></category>
                      </categories>
                    </caps>
                    """, "application/xml");
            }

            var items = string.Concat(_offered.Select(release =>
                $"""<item><title>{release.Title}</title><guid>{release.Guid}</guid><pubDate>Mon, 05 Oct 2026 10:00:00 +0000</pubDate><category>{release.Category}</category><enclosure url="{NzbAddress(release)}" length="{release.Length}" /></item>"""));
            return Results.Text("<rss><channel>" + items + "</channel></rss>", "application/xml");
        }

        // Takes the request, not the context, so its result is written.
        private async Task<IResult> Sabnzbd(HttpRequest request)
        {
            var form = await request.ReadFormAsync();
            if (form["apikey"] != ClientKey)
            {
                return Json("""{"status":false,"error":"API Key Incorrect"}""");
            }

            switch (form["mode"].ToString())
            {
                case "addurl":
                    // The job finishes at once: its file appears, then the history reports it.
                    var address = form["name"].ToString();
                    if (_offered.FirstOrDefault(offer => NzbAddress(offer).ToString() == address) is not { } release)
                    {
                        return Json("""{"status":false,"error":"Unknown NZB"}""");
                    }

                    _grabbed.Enqueue(address);
                    if (HoldDownloads)
                    {
                        _held[release.Guid] = release;
                    }
                    else
                    {
                        Complete(release);
                    }

                    return Json("{\"status\":true,\"nzo_ids\":[\"SABnzbd_nzo_" + release.Guid + "\"]}");
                case "queue" or "history" when form["name"].ToString() is "delete" or "pause" or "resume":
                    _commands.Enqueue(form["mode"] + ":" + form["name"] + ":" + form["value"]);
                    if (form["name"] == "delete")
                    {
                        _held.TryRemove(form["value"].ToString().Replace("SABnzbd_nzo_", "", StringComparison.Ordinal), out _);
                        _completed.TryRemove(form["value"].ToString().Replace("SABnzbd_nzo_", "", StringComparison.Ordinal), out _);
                    }

                    return Json("""{"status":true}""");
                case "queue":
                    var percent = HeldPercent;
                    var queued = _held.Values.Select(waiting =>
                        "{\"nzo_id\":\"SABnzbd_nzo_" + waiting.Guid + "\",\"filename\":\"" + waiting.Title + "\",\"percentage\":\"" + percent + "\",\"mb\":\"1000\",\"mbleft\":\"" + (1000 - 10 * percent)
                        + "\",\"timeleft\":\"0:01:00\",\"status\":\"Downloading\"}");
                    return Json("{\"queue\":{\"paused\":false,\"slots\":[" + string.Join(',', queued) + "]}}");
                case "status":
                    return Json("""{"status":{"completedir":"/downloads"}}""");
                case "get_config":
                    return Json("""{"config":{"categories":[{"name":"*","dir":""},{"name":"movies","dir":""},{"name":"tv","dir":""}]}}""");
                case "history":
                    var slots = _completed.Values.Select(done => FailDownloads
                        ? "{\"nzo_id\":\"SABnzbd_nzo_" + done.Guid + "\",\"name\":\"" + done.Title + "\",\"status\":\"Failed\",\"fail_message\":\"Not enough articles\"}"
                        : "{\"nzo_id\":\"SABnzbd_nzo_" + done.Guid + "\",\"name\":\"" + done.Title + "\",\"status\":\"Completed\",\"storage\":\"/downloads/" + done.Title + "\"}");
                    return Json("{\"history\":{\"slots\":[" + string.Join(',', slots) + "]}}");
                default:
                    return Json("""{"version":"4.5.0"}""");
            }
        }

        // The job's files appear in the downloads folder.
        private void Complete(FakeRelease release)
        {
            var folder = Directory.CreateDirectory(Path.Combine(Downloads, release.Title)).FullName;
            if (Directory.Exists(release.File))
            {
                foreach (var file in Directory.EnumerateFiles(release.File))
                {
                    File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
                }
            }
            else
            {
                File.Copy(release.File, Path.Combine(folder, release.Title + Path.GetExtension(release.File)), overwrite: true);
            }

            _completed[release.Guid] = release;
        }

        private static IResult Json(string body) => Results.Text(body, "application/json");

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
