using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jularr.E2E.Tests
{
    // TMDB, a Newznab indexer and SABnzbd on loopback, so the separately started Jularr never needs a live service.
    internal sealed class FakeServices : IAsyncDisposable
    {
        public const string TmdbToken = "eyJ-e2e-read-token";
        public const string IndexerKey = "e2e-indexer-key";
        public const string ClientKey = "e2e-client-key";
        public const int MovieId = 10378;
        public const string MovieTitle = "Big Buck Bunny";
        public const string Release = "Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR";

        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _grabbed = new();
        private volatile bool _completed;

        private FakeServices(WebApplication app, Uri address, string downloads)
        {
            _app = app;
            Address = address;
            Downloads = downloads;
        }

        public Uri Address { get; }

        // Where SABnzbd puts finished jobs; Jularr maps "/downloads" here.
        public string Downloads { get; }

        public IReadOnlyCollection<string> Grabbed => _grabbed;

        // The file a finished download leaves behind.
        public Func<string, Task>? Complete { get; set; }

        public static async Task<FakeServices> StartAsync(string downloads)
        {
            var port = FreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:" + port);
            var app = builder.Build();
            var services = new FakeServices(app, new Uri("http://127.0.0.1:" + port + "/"), downloads);

            // Every call joins the test output, so a failure shows what Jularr asked for.
            app.Use(async (http, next) =>
            {
                try
                {
                    await next(http);
                }
                catch (Exception exception)
                {
                    Console.WriteLine("[fake] " + exception);
                    throw;
                }
                finally
                {
                    Console.WriteLine("[fake] " + http.Request.Method + " " + http.Request.Path + " " + http.Response.StatusCode);
                }
            });
            app.MapGet("/tmdb/3/{**path}", services.Tmdb);
            app.MapGet("/newznab/api", services.Newznab);
            app.MapGet("/newznab/getnzb/{id}", (string id) => Results.Text("<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\"></nzb>", "application/x-nzb"));
            app.MapPost("/sabnzbd/api", services.Sabnzbd);
            await app.StartAsync();
            return services;
        }

        public Uri Tmdb3 => new(Address, "tmdb/3/");

        public ValueTask DisposeAsync() => _app.DisposeAsync();

        private IResult Tmdb(HttpContext http, string path)
        {
            if (http.Request.Headers.Authorization.ToString() != "Bearer " + TmdbToken)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            const string movie = """{"id":10378,"title":"Big Buck Bunny","original_title":"Big Buck Bunny","release_date":"2008-05-30","overview":"A giant rabbit takes revenge on three bullies.","original_language":"en","genres":[{"id":16,"name":"Animation"}],"external_ids":{"imdb_id":"tt1254207"}}""";
            return path switch
            {
                "search/movie" when http.Request.Query["query"].ToString().Contains("Bunny", StringComparison.OrdinalIgnoreCase) => Json("""{"results":[""" + movie + "]}"),
                "search/movie" or "search/tv" => Json("""{"results":[]}"""),
                "movie/10378" => Json(movie),
                _ when path.StartsWith("trending/", StringComparison.Ordinal) || path.StartsWith("discover/", StringComparison.Ordinal) || path.EndsWith("/top_rated", StringComparison.Ordinal)
                    || path == "movie/upcoming" => Json(path.Contains("movie", StringComparison.Ordinal) ? """{"results":[""" + movie + "]}" : """{"results":[]}"""),
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
                      </searching>
                      <categories><category id="2000" name="Movies"><subcat id="2040" name="HD" /></category></categories>
                    </caps>
                    """, "application/xml");
            }

            var address = new Uri(Address, "newznab/getnzb/bunny");
            return Results.Text($"""
                <rss><channel>
                  <item><title>{Release}</title><guid>bunny</guid><pubDate>Mon, 05 Oct 2026 10:00:00 +0000</pubDate><category>2040</category><enclosure url="{address}" length="400000000" /></item>
                  <item><title>Other.Movie.2008.1080p.WEB-DL.H264-JLR</title><guid>other</guid><pubDate>Mon, 05 Oct 2026 10:00:00 +0000</pubDate><category>2040</category><enclosure url="{new Uri(Address, "newznab/getnzb/other")}" length="400000000" /></item>
                </channel></rss>
                """, "application/xml");
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
                    _grabbed.Enqueue(form["name"].ToString());

                    // The job finishes at once: its files appear, then the history reports them.
                    if (Complete is { } complete)
                    {
                        await complete(Path.Combine(Downloads, Release));
                    }

                    _completed = true;
                    return Json("""{"status":true,"nzo_ids":["SABnzbd_nzo_e2e"]}""");
                case "queue":
                    return Json("""{"queue":{"paused":false,"slots":[]}}""");
                case "history":
                    return Json(_completed
                        ? $$$"""{"history":{"slots":[{"nzo_id":"SABnzbd_nzo_e2e","name":"{{{Release}}}","status":"Completed","storage":"/downloads/{{{Release}}}"}]}}"""
                        : """{"history":{"slots":[]}}""");
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
