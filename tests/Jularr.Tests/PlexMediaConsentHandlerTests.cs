using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Devices;
using Jularr.Web.Features.ExternalPlayback.Plex;
using Jularr.Web.Features.Plex;
using Jularr.Web.Pages.Account;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jularr.Tests;

[TestClass]
public sealed class PlexMediaConsentHandlerTests
{
    [TestMethod]
    public async Task MediaPin_FailedStorageWrite_RetriesWithoutReplayingCompletedGrant()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-media-pin-{Guid.NewGuid():N}");
        var database = Path.Combine(
            Path.GetTempPath(), $"jularr-plex-pin-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={database};Foreign Keys=True")
                .Options;
            await using var db = new AppDbContext(options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var auth = new OwnerAuthService(
                db, new PasswordHasher<OwnerAccount>());
            var owner = await auth.CreateOwnerAsync(
                "owner", "a sufficiently long owner password");

            const string nonce = "unique-plex-browser-nonce";
            var attempt = new PlexLoginAttempt
            {
                PinId = 124,
                ClientIdentifier = "plex-test-client",
                BrowserNonceHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(nonce))),
                StartedAccountId = owner.Id,
                Purpose = "media",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
            };
            db.PlexLoginAttempts.Add(attempt);
            await db.SaveChangesAsync();

            using var http = new HttpClient(new Handler(request =>
                request.RequestUri!.AbsolutePath switch
                {
                    "/api/v2/pins/124" => Json("""{"authToken":"test-private-token"}"""),
                    "/api/v2/user" => Json("""{"id":500,"username":"viewer"}"""),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                }))
            {
                BaseAddress = new Uri("https://plex.tv/")
            };
            var settings = new PlexIdentitySettingsStore(
                new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Plex:ClientIdentifier"] = "plex-test-client",
                        ["Plex:MediaConnectionEnabled"] = "true"
                    }).Build(),
                Path.Combine(root, "settings"));
            var protection = new EphemeralDataProtectionProvider();
            var connections = new PlexProfileConnectionStore(
                protection, Path.Combine(root, "profiles"));
            var page = new PlexModel(
                db, auth,
                new MediaCapabilityStore(Path.Combine(root, "capabilities")),
                new PlexAuthClient(http),
                new SecurityEventLog(TimeProvider.System),
                settings, connections)
            {
                Flow = attempt.Id
            };
            var context = new DefaultHttpContext
            {
                User = OwnerAuthService.CreatePrincipal(owner)
            };
            context.Request.Headers.Cookie = "Jularr.Plex.Flow=" + nonce;
            page.PageContext = new PageContext(
                new ActionContext(context, new RouteData(), new PageActionDescriptor()));

            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "profiles"), "not-a-directory");

            Assert.IsInstanceOfType<PageResult>(
                await page.OnGetFinishMediaAsync(CancellationToken.None));
            Assert.AreEqual("media",
                (await db.PlexLoginAttempts.AsNoTracking()
                    .SingleAsync(x => x.Id == attempt.Id)).Purpose);
            Assert.IsNull(await connections.GetStatusAsync(owner.Id));

            File.Delete(Path.Combine(root, "profiles"));
            Assert.IsInstanceOfType<RedirectToPageResult>(
                await page.OnGetFinishMediaAsync(CancellationToken.None));
            Assert.IsFalse(await db.PlexLoginAttempts.AsNoTracking()
                .AnyAsync(x => x.Id == attempt.Id));
            Assert.IsTrue((await connections.GetStatusAsync(owner.Id))!.IsUsable);

            Assert.IsInstanceOfType<BadRequestResult>(
                await page.OnGetFinishMediaAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            File.Delete(database);
        }
    }

    private static HttpResponseMessage Json(string data) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(data, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
