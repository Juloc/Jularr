using System.Net;
using System.Text;
using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Branding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jularr.Tests;

/// <summary>The instance branding of #876: what a logo may be, what is stored and what the browser is given.</summary>
[TestClass]
public sealed class InstanceBrandingTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private const string CleanSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 8 8\"><defs><path id=\"a\" d=\"M0 0h8v8z\"/></defs><use href=\"#a\" fill=\"#0a7\"/></svg>";

    [TestMethod]
    public void ANameIsTrimmedBoundedAndNeverTheProductNameAgain()
    {
        Assert.IsTrue(BrandingValidation.TryNormalizeName("  Casa Media  ", out var name));
        Assert.AreEqual("Casa Media", name);

        Assert.IsTrue(BrandingValidation.TryNormalizeName("   ", out var empty));
        Assert.IsNull(empty, "An empty name means no custom name.");
        Assert.IsTrue(BrandingValidation.TryNormalizeName("jularr", out var product));
        Assert.IsNull(product, "There is no \"Jularr by Jularr\".");

        Assert.IsFalse(BrandingValidation.TryNormalizeName(new string('x', BrandingValidation.MaxNameLength + 1), out _));
        Assert.IsFalse(BrandingValidation.TryNormalizeName("bell\u0007", out _));
        Assert.IsTrue(BrandingValidation.TryNormalizeName(new string('x', BrandingValidation.MaxNameLength), out _));
    }

    [TestMethod]
    public void ALogoIsRecognisedByItsBytesAndAVectorWithAnythingActiveIsRefused()
    {
        Assert.AreEqual(BrandingLogoProblem.None, BrandingValidation.Validate(Png, out var png));
        Assert.AreEqual("image/png", png);
        Assert.AreEqual(BrandingLogoProblem.None, BrandingValidation.Validate([0xFF, 0xD8, 0xFF, 0xE0, 0], out var jpeg));
        Assert.AreEqual("image/jpeg", jpeg);
        Assert.AreEqual(BrandingLogoProblem.None, BrandingValidation.Validate("RIFF\0\0\0\0WEBPVP8 "u8, out var webp));
        Assert.AreEqual("image/webp", webp);
        Assert.AreEqual(BrandingLogoProblem.None, BrandingValidation.Validate(Encoding.UTF8.GetBytes(CleanSvg), out var svg));
        Assert.AreEqual("image/svg+xml", svg);

        foreach (var unsafeSvg in new[]
        {
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><div/></foreignObject></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"javascript:alert(1)\"><rect/></a></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://evil.example/x.png\"/></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"data:image/png;base64,AAAA\"/></svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"fill:url(https://evil.example/p)\"/></svg>",
            "<!DOCTYPE svg [<!ENTITY x \"boom\">]><svg xmlns=\"http://www.w3.org/2000/svg\"/>"
        })
        {
            Assert.AreEqual(BrandingLogoProblem.UnsafeVector, BrandingValidation.Validate(Encoding.UTF8.GetBytes(unsafeSvg), out var refused), unsafeSvg);
            Assert.IsNull(refused);
        }

        Assert.AreEqual(BrandingLogoProblem.UnsupportedType, BrandingValidation.Validate("<html><body>not an image</body></html>"u8, out _));
        Assert.AreEqual(BrandingLogoProblem.Empty, BrandingValidation.Validate([], out _));
        Assert.AreEqual(BrandingLogoProblem.TooLarge, BrandingValidation.Validate(new byte[BrandingValidation.MaxLogoBytes + 1], out _));
    }

    [TestMethod]
    public void OnlyALogoWithARealAlphaChannelCanBeRecolouredAndTheDecisionNeverChangesTheFile()
    {
        Assert.IsTrue(BrandingValidation.CanRecolour(PngOf(6), "image/png"), "Truecolour with alpha.");
        Assert.IsTrue(BrandingValidation.CanRecolour(PngOf(4), "image/png"), "Greyscale with alpha.");
        Assert.IsTrue(BrandingValidation.CanRecolour(PngOf(3, transparency: true), "image/png"), "A palette image with a transparency chunk.");
        Assert.IsFalse(BrandingValidation.CanRecolour(PngOf(3), "image/png"), "An opaque palette image would become a solid square.");
        Assert.IsFalse(BrandingValidation.CanRecolour(PngOf(2), "image/png"));
        Assert.IsFalse(BrandingValidation.CanRecolour(Png, "image/png"), "A truncated header proves nothing.");
        Assert.IsFalse(BrandingValidation.CanRecolour([0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg"), "A JPEG has no alpha.");
        Assert.IsTrue(BrandingValidation.CanRecolour(Encoding.UTF8.GetBytes(CleanSvg), "image/svg+xml"));
        Assert.IsTrue(BrandingValidation.CanRecolour(WebpOf("VP8X", 0x10), "image/webp"), "Extended WebP with the alpha flag.");
        Assert.IsFalse(BrandingValidation.CanRecolour(WebpOf("VP8X", 0x00), "image/webp"));
        Assert.IsFalse(BrandingValidation.CanRecolour(WebpOf("VP8 ", 0x10), "image/webp"), "Lossy plain WebP has no alpha.");
        Assert.IsFalse(BrandingValidation.CanRecolour(Png, null));
    }

    [TestMethod]
    public void TheLogoIsOnlyRecolouredWhileTheBrandColourIsOnTheChoiceIsMadeAndTheLogoCanBe()
    {
        var all = new InstanceBrandingSettings("Casa", true, 160, true, 1, RecolourLogo: true, LogoRecolourable: true);

        Assert.IsTrue(all.RecolourLogoActive);
        Assert.IsFalse((all with { HueBranding = false }).RecolourLogoActive, "Without the brand colour the logo keeps its own colours.");
        Assert.IsFalse((all with { RecolourLogo = false }).RecolourLogoActive, "The brand colour alone leaves the logo as it is.");
        Assert.IsFalse((all with { LogoRecolourable = false }).RecolourLogoActive, "A logo that cannot give a silhouette falls back to the original.");
        Assert.IsFalse((all with { HasLogo = false }).RecolourLogoActive);
    }

    [TestMethod]
    public async Task TheStoreRemembersWhetherTheUploadCanBeRecolouredAndKeepsTheChoiceWhenTheLogoChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = fixture.Store;

        await store.SetLogoAsync(PngOf(6), CancellationToken.None);
        await store.SaveIdentityAsync("Casa", true, 160, true, CancellationToken.None);
        var coloured = await store.GetAsync(CancellationToken.None);
        Assert.IsTrue(coloured.LogoRecolourable);
        Assert.IsTrue(coloured.RecolourLogoActive);
        CollectionAssert.AreEqual(PngOf(6), (await store.GetLogoAsync(CancellationToken.None))!.Bytes, "The uploaded file is never changed by recolouring.");

        await store.SetLogoAsync([0xFF, 0xD8, 0xFF, 0xE0, 0, 0], CancellationToken.None);
        var jpeg = await store.GetAsync(CancellationToken.None);
        Assert.IsFalse(jpeg.LogoRecolourable);
        Assert.IsTrue(jpeg.RecolourLogo, "The owner's choice is kept; it simply cannot apply to this logo.");
        Assert.IsFalse(jpeg.RecolourLogoActive);

        await store.RemoveLogoAsync(CancellationToken.None);
        Assert.IsFalse((await store.GetAsync(CancellationToken.None)).LogoRecolourable);
    }

    [TestMethod]
    public void AHueIsASeedColourOnlyWhileHueBrandingIsOn()
    {
        var on = new InstanceBrandingSettings(null, true, 160, false, 0);
        var off = on with { HueBranding = false };

        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(on.HueSeed!, "^#[0-9a-f]{6}$"));
        Assert.IsNull(off.HueSeed, "The configured hue is kept but not used while the switch is off.");
        Assert.AreNotEqual(BrandingColor.SeedOf(0), BrandingColor.SeedOf(120));
        Assert.IsFalse(InstanceBrandingSettings.Default.IsCustom);
        Assert.IsTrue(new InstanceBrandingSettings("Casa", false, null, false, 0).IsCustom);
        Assert.AreEqual("Jularr", InstanceBrandingSettings.Default.DisplayName);
    }

    [TestMethod]
    public async Task TheStoreKeepsNameLogoAndHueApartAndAWriteIsSeenAtOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = fixture.Store;

        Assert.AreEqual(InstanceBrandingSettings.Default, await store.GetAsync(CancellationToken.None));

        await store.SaveIdentityAsync("Casa Media", true, 160, false, CancellationToken.None);
        var named = await store.GetAsync(CancellationToken.None);
        Assert.AreEqual("Casa Media", named.Name);
        Assert.IsTrue(named.HueBranding);
        Assert.AreEqual(160, named.Hue);
        Assert.IsFalse(named.HasLogo);

        Assert.AreEqual(BrandingLogoProblem.None, await store.SetLogoAsync(Png, CancellationToken.None));
        var withLogo = await store.GetAsync(CancellationToken.None);
        Assert.IsTrue(withLogo.HasLogo);
        Assert.AreEqual(1, withLogo.LogoVersion);
        Assert.AreEqual("Casa Media", withLogo.Name, "Uploading a logo leaves the name alone.");

        await store.SetLogoAsync(Png, CancellationToken.None);
        Assert.AreEqual(2, (await store.GetAsync(CancellationToken.None)).LogoVersion, "A new upload is a new address, so browsers load it.");
        var stored = await store.GetLogoAsync(CancellationToken.None);
        Assert.AreEqual("image/png", stored!.ContentType);
        CollectionAssert.AreEqual(Png, stored.Bytes);

        Assert.AreEqual(BrandingLogoProblem.UnsupportedType, await store.SetLogoAsync("plain text"u8.ToArray(), CancellationToken.None), "A refused upload changes nothing.");
        Assert.AreEqual(2, (await store.GetAsync(CancellationToken.None)).LogoVersion);

        await store.SaveIdentityAsync(null, false, null, false, CancellationToken.None);
        var unnamed = await store.GetAsync(CancellationToken.None);
        Assert.IsNull(unnamed.Name);
        Assert.IsTrue(unnamed.HasLogo, "Saving the identity does not drop the logo.");

        await store.RemoveLogoAsync(CancellationToken.None);
        Assert.IsFalse((await store.GetAsync(CancellationToken.None)).HasLogo);
        Assert.IsNull(await store.GetLogoAsync(CancellationToken.None));

        await store.SaveIdentityAsync("Again", true, 10, false, CancellationToken.None);
        await store.ResetAsync(CancellationToken.None);
        Assert.AreEqual(InstanceBrandingSettings.Default, await store.GetAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task TheDatabaseRefusesWhatTheStoreWouldNeverWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        async Task<string?> Violation(string sql)
        {
            try
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
                return null;
            }
            catch (PostgresException exception)
            {
                return exception.ConstraintName;
            }
        }

        Assert.AreEqual("CK_InstanceBranding_Hue", await Violation("INSERT INTO \"InstanceBranding\" (\"Id\", \"Hue\", \"UpdatedAt\") VALUES (1, 400, now())"));
        Assert.AreEqual("CK_InstanceBranding_SingleRow", await Violation("INSERT INTO \"InstanceBranding\" (\"Id\", \"UpdatedAt\") VALUES (2, now())"));
        Assert.AreEqual("CK_InstanceBranding_Name", await Violation($"INSERT INTO \"InstanceBranding\" (\"Id\", \"Name\", \"UpdatedAt\") VALUES (1, '{new string('x', 41)}', now())"));
        Assert.AreEqual("CK_InstanceBranding_Logo", await Violation("INSERT INTO \"InstanceBranding\" (\"Id\", \"Logo\", \"UpdatedAt\") VALUES (1, '\\x00'::bytea, now())"), "A logo needs its content type.");
        Assert.AreEqual("CK_InstanceBranding_Logo", await Violation("INSERT INTO \"InstanceBranding\" (\"Id\", \"Logo\", \"LogoContentType\", \"UpdatedAt\") VALUES (1, decode(repeat('00', 524289), 'hex'), 'image/png', now())"), "A logo is never larger than the limit.");
    }

    [TestMethod]
    public async Task TheLogoIsPublicSandboxedAndCachedByVersionAndTheManifestFollowsTheName()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var server = await fixture.StartServerAsync();
        using var client = server.CreateClient();

        using var none = await client.GetAsync(BrandingEndpoints.LogoPath);
        Assert.AreEqual(HttpStatusCode.NotFound, none.StatusCode);
        using var plain = await client.GetAsync(BrandingEndpoints.ManifestPath);
        Assert.AreEqual("Jularr", JsonDocument.Parse(await plain.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString(), "Without branding the manifest is the file as it ships.");

        await fixture.Store.SaveIdentityAsync("Casa Media Center Of Everything", false, null, false, CancellationToken.None);
        await fixture.Store.SetLogoAsync(Encoding.UTF8.GetBytes(CleanSvg), CancellationToken.None);

        using var logo = await client.GetAsync(BrandingEndpoints.LogoPath);
        Assert.AreEqual(HttpStatusCode.OK, logo.StatusCode, "The login page shows the logo before anyone signs in.");
        Assert.AreEqual("image/svg+xml", logo.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("nosniff", logo.Headers.GetValues("X-Content-Type-Options").Single());
        StringAssert.Contains(logo.Headers.GetValues("Content-Security-Policy").Single(), "sandbox");
        StringAssert.Contains(logo.Headers.CacheControl!.ToString(), "max-age=86400");
        var etag = logo.Headers.ETag!.Tag;

        using var conditional = new HttpRequestMessage(HttpMethod.Get, BrandingEndpoints.LogoPath);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var notModified = await client.SendAsync(conditional);
        Assert.AreEqual(HttpStatusCode.NotModified, notModified.StatusCode);

        using var branded = await client.GetAsync(BrandingEndpoints.ManifestPath);
        var manifest = JsonDocument.Parse(await branded.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual("Casa Media Center Of Everything", manifest.GetProperty("name").GetString());
        Assert.AreEqual("Casa Media C", manifest.GetProperty("short_name").GetString());
        Assert.AreEqual("application/manifest+json", branded.Content.Headers.ContentType!.MediaType);
        Assert.IsTrue(manifest.GetProperty("icons").GetArrayLength() > 0, "Everything else of the manifest is kept.");
    }

    private static byte[] PngOf(byte colourType, bool transparency = false)
    {
        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 1, 0, 0, 0, 1, 8, colourType, 0, 0, 0, 0, 0, 0, 0 };
        var chunks = transparency ? new byte[] { 0, 0, 0, 1, (byte)'t', (byte)'R', (byte)'N', (byte)'S', 0, 0, 0, 0, 0 } : [];
        return [.. header, .. chunks, 0, 0, 0, 0, (byte)'I', (byte)'D', (byte)'A', (byte)'T', 0, 0, 0, 0];
    }

    private static byte[] WebpOf(string format, byte flags)
    {
        var bytes = new byte[32];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        Encoding.ASCII.GetBytes(format).CopyTo(bytes, 12);
        bytes[20] = flags;
        bytes[24] = flags;
        return bytes;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string dataSource;
        private ServiceProvider? services;

        private Fixture(string dataSource, string connectionString)
        {
            this.dataSource = dataSource;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public InstanceBrandingStore Store { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var dataSource = $"Data Source=branding-{Guid.NewGuid():N}.db";
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(dataSource).Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            var fixture = new Fixture(dataSource, db.Database.GetConnectionString()!);
            var collection = new ServiceCollection();
            collection.AddDbContext<AppDbContext>(options => options.UseSqlite(dataSource));
            collection.AddSingleton(TimeProvider.System);
            collection.AddSingleton<InstanceBrandingStore>();
            fixture.services = collection.BuildServiceProvider();
            fixture.Store = fixture.services.GetRequiredService<InstanceBrandingStore>();
            return fixture;
        }

        public async Task<TestServer> StartServerAsync()
        {
            var host = await new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseTestServer()
                    .UseContentRoot(WebRoot())
                    .ConfigureServices(collection =>
                    {
                        collection.AddRouting();
                        collection.AddDbContext<AppDbContext>(options => options.UseSqlite(dataSource));
                        collection.AddSingleton(TimeProvider.System);
                        collection.AddSingleton(Store);
                    })
                    .Configure(app =>
                    {
                        app.UseBrandedManifest();
                        app.UseStaticFiles();
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapBranding());
                    }))
                .StartAsync();
            return host.GetTestServer();
        }

        public async ValueTask DisposeAsync()
        {
            if (services is not null)
            {
                await services.DisposeAsync();
            }
        }

        private static string WebRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "src", "Jularr.Web");
        }
    }
}
