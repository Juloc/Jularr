using System.IO.Compression;
using System.Net.Http.Headers;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace Jularr.Backend.Tests
{
    // Pages and assets leave compressed; the live event stream and media are sent as they are.
    [TestClass]
    public sealed class CompressionTests
    {
        private static readonly string Page = "<!DOCTYPE html><main>" + string.Concat(Enumerable.Repeat("<article class=\"media-card\">Jularr</article>", 400)) + "</main>";

        [TestMethod]
        public async Task Pages_LeaveWithBrotliOrGzip_AndDecodeToTheSameHtml()
        {
            await using var app = await StartAsync();
            foreach (var (encoding, decode) in new (string, Func<Stream, Stream>)[]
            {
                ("br", stream => new BrotliStream(stream, CompressionMode.Decompress)),
                ("gzip", stream => new GZipStream(stream, CompressionMode.Decompress)),
            })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/page");
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));
                using var response = await app.GetTestClient().SendAsync(request);
                CollectionAssert.AreEqual(new[] { encoding }, response.Content.Headers.ContentEncoding.ToArray());
                var compressed = await response.Content.ReadAsByteArrayAsync();
                Assert.IsTrue(compressed.Length < Page.Length / 10, $"{encoding}: {compressed.Length} bytes for {Page.Length}");
                using var reader = new StreamReader(decode(new MemoryStream(compressed)));
                Assert.AreEqual(Page, await reader.ReadToEndAsync());
            }
        }

        [TestMethod]
        public async Task LiveEventsAndMedia_AreNotCompressed()
        {
            await using var app = await StartAsync();
            foreach (var path in new[] { "/live", "/video" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
                using var response = await app.GetTestClient().SendAsync(request);
                Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count, path);
            }
        }

        private static async Task<WebApplication> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddJularrCompression();
            var app = builder.Build();
            app.UseResponseCompression();
            app.MapGet("/page", () => Results.Content(Page, "text/html; charset=utf-8"));
            app.MapGet("/live", () => Results.Content("event: ping\ndata: " + new string('x', 4000) + "\n\n", "text/event-stream"));
            app.MapGet("/video", () => Results.Bytes(new byte[4000], "video/webm"));
            await app.StartAsync();
            return app;
        }
    }
}
