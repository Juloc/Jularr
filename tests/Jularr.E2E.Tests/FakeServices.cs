using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jularr.E2E.Tests
{
    // A release the fake indexer offers and the file its finished download contains.
    internal sealed record FakeRelease(string Title, string Guid, int Category, string File);

    // TMDB, a Newznab indexer and SABnzbd on loopback, so the separately started Jularr never needs a live service.
    internal sealed class FakeServices : IAsyncDisposable
    {
        public const string TmdbToken = "eyJ-e2e-read-token";
        public const string IndexerKey = "e2e-indexer-key";
        public const string ClientKey = "e2e-client-key";
        public const string MovieTitle = "Big Buck Bunny";
        public const string SeriesTitle = "Jularr Test Series";

        private const string Movie = """{"id":10378,"title":"Big Buck Bunny","original_title":"Big Buck Bunny","release_date":"2008-05-30","overview":"A giant rabbit takes revenge on three bullies.","original_language":"en","genres":[{"id":16,"name":"Animation"}],"external_ids":{"imdb_id":"tt1254207"}}""";
        private const string Series = """{"id":99001,"name":"Jularr Test Series","original_name":"Jularr Test Series","first_air_date":"2020-04-01","overview":"Two test episodes.","original_language":"en","origin_country":["DE"],"external_ids":{"tvdb_id":990001},"seasons":[{"season_number":1,"name":"Season 1","air_date":"2020-04-01","episode_count":2}]}""";

        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _grabbed = new();
        private readonly ConcurrentDictionary<string, FakeRelease> _completed = new();
        private volatile FakeRelease[] _offered = [];

        private FakeServices(WebApplication app, Uri address, string downloads)
        {
            _app = app;
            Address = address;
            Downloads = downloads;
        }

        public Uri Address { get; }

        public Uri Tmdb3 => new(Address, "tmdb/3/");

        // Where SABnzbd puts finished jobs; Jularr maps "/downloads" here.
        public string Downloads { get; }

        public IReadOnlyCollection<string> Grabbed => _grabbed;

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
            app.MapGet("/newznab/api", services.Newznab);
            app.MapGet("/newznab/getnzb/{id}", (string id) => Results.Text("<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"></nzb>", "application/x-nzb"));
            app.MapPost("/sabnzbd/api", services.Sabnzbd);
            await app.StartAsync();
            return services;
        }

        // Releases appear only now, so no automatic search grabs before the manual one.
        public void Offer(params FakeRelease[] releases) => _offered = releases;

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
                "search/tv" => Json(query.Contains("Test Series", StringComparison.OrdinalIgnoreCase) ? "{\"results\":[" + Series + "]}" : "{\"results\":[]}"),
                "movie/10378" => Json(Movie),
                "tv/99001" => Json(Series),
                "tv/99001/season/1" => Json("""{"episodes":[{"season_number":1,"episode_number":1,"name":"Pilot","air_date":"2020-04-01"},{"season_number":1,"episode_number":2,"name":"Second","air_date":"2020-04-08"}]}"""),
                _ when path.StartsWith("trending/", StringComparison.Ordinal) || path.StartsWith("discover/", StringComparison.Ordinal) || path.EndsWith("/top_rated", StringComparison.Ordinal)
                    || path == "movie/upcoming" => Json(path.Contains("movie", StringComparison.Ordinal) ? "{\"results\":[" + Movie + "]}" : "{\"results\":[" + Series + "]}"),
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
                        <movie-search available="yes" supportedParams="q,imdbid" />
                        <tv-search available="yes" supportedParams="q,season,ep" />
                      </searching>
                      <categories>
                        <category id="2000" name="Movies"><subcat id="2040" name="HD" /></category>
                        <category id="5000" name="TV"><subcat id="5040" name="HD" /></category>
                      </categories>
                    </caps>
                    """, "application/xml");
            }

            var items = string.Concat(_offered.Select(release =>
                $"""<item><title>{release.Title}</title><guid>{release.Guid}</guid><pubDate>Mon, 05 Oct 2026 10:00:00 +0000</pubDate><category>{release.Category}</category><enclosure url="{NzbAddress(release)}" length="400000000" /></item>"""));
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
                    var folder = Directory.CreateDirectory(Path.Combine(Downloads, release.Title)).FullName;
                    File.Copy(release.File, Path.Combine(folder, release.Title + Path.GetExtension(release.File)), overwrite: true);
                    _completed[release.Guid] = release;
                    return Json("{\"status\":true,\"nzo_ids\":[\"SABnzbd_nzo_" + release.Guid + "\"]}");
                case "queue":
                    return Json("""{"queue":{"paused":false,"slots":[]}}""");
                case "history":
                    var slots = _completed.Values.Select(done =>
                        "{\"nzo_id\":\"SABnzbd_nzo_" + done.Guid + "\",\"name\":\"" + done.Title + "\",\"status\":\"Completed\",\"storage\":\"/downloads/" + done.Title + "\"}");
                    return Json("{\"history\":{\"slots\":[" + string.Join(',', slots) + "]}}");
                default:
                    return Json("""{"version":"4.5.0"}""");
            }
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
