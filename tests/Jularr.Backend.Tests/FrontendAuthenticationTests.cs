using System.Net;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Xml.Linq;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Admin.Instance.V1;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class FrontendAuthenticationTests
    {
        [TestMethod]
        [DataRow("en-US")]
        [DataRow("de-DE")]
        public async Task Form_InvariantNumber_BindsWithoutChangingValue(string locale)
        {
            var values = new FormCollection(new Dictionary<string, StringValues> { ["UnitNumber"] = "1.5", ["__Invariant"] = "UnitNumber" });
            var provider = new FormValueProvider(BindingSource.Form, values, CultureInfo.GetCultureInfo(locale));
            var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
            var metadata = new EmptyModelMetadataProvider().GetMetadataForType(typeof(decimal));
            var context = DefaultModelBindingContext.CreateBindingContext(action, provider, metadata, null, "UnitNumber");
            await new DecimalModelBinder(NumberStyles.Float | NumberStyles.AllowThousands, NullLoggerFactory.Instance).BindModelAsync(context);
            Assert.AreEqual(1.5m, context.Result.Model);
        }

        [TestMethod]
        public async Task MediaCard_ResumeAndLanguages_RenderEncodedAccessibleContext()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);
            var assets = scope.ServiceProvider.GetRequiredService<FrontendAssets>();

            // Preference and fallback independently.
            var card = new MediaCard("<Title>", "/Details/1", null, null, "media.state.InProgress", "media.continue", "/Player/2")
                .SeriesStatus("media.series.Continuing").Availability(new MediaAvailability(assets, MediaAvailabilityState.Partial, 18, 24))
                .Languages(assets, new MediaLanguage("flag-de", "design.languageGerman"), 3, null, 6)
                .Genres(new ValueText("<Genre>"), new ValueText("Second"), new ValueText("Third"), new ValueText("Fourth")).Continue(30000, 90000);
            using var writer = new StringWriter();
            card.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();
            StringAssert.Contains(html, "&lt;Title&gt;");
            StringAssert.Contains(html, "class=\"media-card__series\"><span class=\"tag\">" + texts["media.series.Continuing"]);
            StringAssert.Contains(html, "stroke-dasharray=\"75 100\"");
            StringAssert.Contains(html, "#icon-flag-de");
            StringAssert.Contains(html, "aria-label=\"" + string.Format(CultureInfo.CurrentCulture, texts["media.languagePreferred"], texts["media.dubs"], texts["design.languageGerman"]) + "\"");
            StringAssert.Contains(html, "aria-label=\"" + string.Format(CultureInfo.CurrentCulture, texts["media.languageCount"], texts["media.subs"], 6) + "\"");
            StringAssert.Contains(html, "&lt;Genre&gt;");
            Assert.IsFalse(html.Contains("Fourth", StringComparison.Ordinal));
            StringAssert.Contains(html, "<progress max=\"90000\" value=\"30000\" aria-label=\"" + texts["media.progress"] + "\"");
            StringAssert.Contains(html, string.Format(CultureInfo.CurrentCulture, texts["media.minutesLeft"], 1));
            StringAssert.Contains(html, "class=\"media-card__action-area\"><a class=\"button button--primary media-card__action\" href=\"/Player/2\"");
            Assert.IsFalse(html.Contains("class=\"media-card__status\"", StringComparison.Ordinal));

            // Unknown duration has no percentage.
            writer.GetStringBuilder().Clear();
            new MediaCard("Book", "/Details/1", null, null, "media.state.InProgress").Continue(30000, null).WriteTo(writer, HtmlEncoder.Default, texts);
            Assert.IsFalse(writer.ToString().Contains("<progress", StringComparison.Ordinal));

            // Both tags, ordinary fallback links.
            writer.GetStringBuilder().Clear();
            card.Preview("preview-test").WriteTo(writer, HtmlEncoder.Default, texts);
            html = writer.ToString();
            Assert.AreEqual(2, Regex.Matches(html, "data-preview-open=\"preview-test\"").Count);
            Assert.AreEqual(2, Regex.Matches(html, "aria-haspopup=\"dialog\"").Count);
            Assert.IsFalse(Regex.IsMatch(html, "<a[^>]*>[^<]*<a"));

            // Inert artwork, no embedded player.
            writer.GetStringBuilder().Clear();
            new MediaPreview("preview-test", assets, "<Title>", null, null, null, "<Overview>")
                .Trailer("ayi6VfCKBcA").WriteTo(writer, HtmlEncoder.Default, texts);
            html = writer.ToString();
            StringAssert.Contains(html, "<template id=\"preview-test\">");
            StringAssert.Contains(html, "&lt;Overview&gt;");
            StringAssert.Contains(html, "data-trailer-id=\"ayi6VfCKBcA\"");
            Assert.IsFalse(html.Contains("<iframe", StringComparison.Ordinal));
            Assert.ThrowsExactly<ArgumentException>(() => new MediaPreview("test", assets, "Title", null, null, null, null).Trailer("../../bad"));
        }

        [TestMethod]
        [DataRow("Development", HttpStatusCode.OK)]
        [DataRow("Production", HttpStatusCode.NotFound)]
        public async Task Design_EnvironmentGate_OnlyRendersDevelopmentExamples(string environment, HttpStatusCode expected)
        {
            await using var host = await Host.CreateAsync(razor: true);
            host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName = environment;
            using var response = await host.SendAsync(HttpMethod.Get, "/Design");
            Assert.AreEqual(expected, response.StatusCode);
            if (expected != HttpStatusCode.OK) return;

            // Real components, no playback client.
            var html = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "data-accent-input=\"true\"");

            // Original artwork uses real assets.
            foreach (var artwork in new[] { "washi", "blossom-branch", "hero-fuji", "hero-torii", "heroine" })
            {
                var image = Regex.Match(html, "src=\"(/build/assets/" + artwork + "-[^\"]+\\.webp)\"");
                Assert.IsTrue(image.Success, artwork);
            }

            Assert.IsFalse(html.Contains("data-transparency-", StringComparison.Ordinal));
            StringAssert.Contains(html, "aria-invalid=\"true\"");
            StringAssert.Contains(html, "media-hero--placeholder");
            StringAssert.Contains(html, "appearance-approved-");
            StringAssert.Contains(html, "data-start-url=\"\"");
            StringAssert.Contains(html, "class=\"tag tag--accent\"");
            StringAssert.Contains(html, "role=\"switch\"");
            StringAssert.Contains(html, "class=\"ui-field__label\"");
            StringAssert.Contains(html, "<fieldset class=\"ui-field__control\"><legend>");
            StringAssert.Contains(html, "class=\"navigation-tabs\"");
            StringAssert.Contains(html, "your-name-poster-");
            StringAssert.Contains(html, "suzume-poster-");
            StringAssert.Contains(html, "media-card--continue");
            StringAssert.Contains(html, "media-card--library");
            StringAssert.Contains(html, "class=\"button button--primary media-card__action\" type=\"button\" disabled=\"disabled\"");
            StringAssert.Contains(html, "data-shelf-direction=\"next\"");
            StringAssert.Contains(html, "class=\"media-shelf__items\" tabindex=\"0\" role=\"region\"");
            foreach (var state in Enum.GetValues<MediaAvailabilityState>())
                StringAssert.Contains(html, "media-availability--" + state.ToString().ToLowerInvariant());
            Assert.AreEqual(1, Regex.Matches(html, "<dialog\\b").Count);
            Assert.AreEqual(9, Regex.Matches(html, "<template\\b").Count);
            StringAssert.Contains(html, "data-preview-open=\"preview-series-Continuing\"");
            StringAssert.Contains(html, "data-trailer-id=\"ayi6VfCKBcA\"");
            Assert.IsFalse(html.Contains("<iframe", StringComparison.Ordinal));
            StringAssert.Contains(html, "Request status");
            StringAssert.Contains(html, "Download status");
            StringAssert.Contains(html, "<progress max=\"6360000\" value=\"3000000\"");
            StringAssert.Contains(html, "poster=\"/build/assets/your-name-backdrop-");
            StringAssert.Contains(html, "class=\"player__audio\"");
            StringAssert.Contains(html, "class=\"player__options\"");
            Assert.AreEqual(1, Regex.Matches(html, "<video\\b").Count);
            var transport = Regex.Match(html, "<div class=\"player__transport\">(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;
            CollectionAssert.AreEqual(new[] { "previous", "back", "toggle", "forward", "next" },
                Regex.Matches(transport, "data-action=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray());
            Assert.AreEqual(2, Regex.Matches(transport, "disabled=\"disabled\"").Count);
            Assert.IsFalse(Regex.IsMatch(html, "<script[^>]*player-[^>]*\\.js"));
            Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        }

        [TestMethod]
        public async Task Razor_PageFolders_PreservePublicRoutes()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (101, 1, 'Route test')");
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            var loginHtml = await login.Content.ReadAsStringAsync();
            StringAssert.Contains(loginHtml, "brand-symbol__original");
            StringAssert.Contains(loginHtml, "brand-symbol__clean");
            StringAssert.Contains(loginHtml, "data-set-skin=\"original\"");
            StringAssert.Contains(loginHtml, "data-set-skin=\"clean\"");

            // Logos share an unshifted viewport.
            foreach (var skin in new[] { "clean", "original" })
            {
                var markup = Regex.Match(loginHtml, "<svg[^>]*class=\"brand-symbol__" + skin + "\"[^>]*>.*?</svg>", RegexOptions.Singleline);
                Assert.IsTrue(markup.Success, skin);
                var svg = XElement.Parse(markup.Value);
                Assert.AreEqual("0 0 48 48", svg.Attribute("viewBox")?.Value, skin);
                Assert.AreEqual("48", svg.Element("use")?.Attribute("width")?.Value, skin);
                Assert.AreEqual("48", svg.Element("use")?.Attribute("height")?.Value, skin);
            }

            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(loginHtml, ("Email", "a@example.test"), ("Password", "test-password-123")));
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);

            // Folder moves preserve URLs.
            foreach (var path in new[] { "/", "/Details/101", "/Watchlist", "/Settings", "/Account/Profiles", "/Account/Sessions", "/Account/Logout" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, path);
                if (path == "/Settings")
                {
                    var html = await page.Content.ReadAsStringAsync();
                    StringAssert.Contains(html, "data-set-skin=\"original\"");
                    StringAssert.Contains(html, "data-set-mode=\"system\"");
                }
            }

            // Player routes take canonical asset identities.
            using var player = await host.SendAsync(HttpMethod.Get, "/Player/101");
            Assert.AreEqual(HttpStatusCode.NotFound, player.StatusCode);

            using var discover = await host.SendAsync(HttpMethod.Get, "/Discover?media=movie");
            Assert.AreEqual(HttpStatusCode.MovedPermanently, discover.StatusCode);
            Assert.AreEqual("/?media=movie", discover.Headers.Location!.OriginalString);
        }

        [TestMethod]
        public async Task Form_PasswordMetadataAndBackendError_RenderAutomaticallyWithoutSecrets()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);

            // Validate through the backend field.
            var data = new RegisterDataV1("new@example.test", "New", "short", 1);
            var validation = Assert.ThrowsExactly<DataValidationException>(data.Validate);
            Assert.AreEqual("Password", validation.Field);
            Assert.AreEqual("too_short", validation.Code);
            new LoginDataV1("a@example.test", "short").Validate();
            new RegisterDataV1("new@example.test", "New", "  valid passphrase  ", 1).Validate();
            Assert.ThrowsExactly<DataValidationException>(() => new RegisterDataV1("new@example.test", "New", new string('x', 129), 1).Validate());

            // The component consumes shared metadata/errors.
            var form = new Form<RegisterDataV1>(fields => fields.Password(item => item.Password, "account.field.password", "new-password"),
                "/Account/Login", "test-antiforgery-token", data, ServiceError.FromException(validation));
            using var writer = new StringWriter();
            form.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();
            StringAssert.Contains(html, "minlength=\"12\"");
            StringAssert.Contains(html, "maxlength=\"128\"");
            StringAssert.Contains(html, "required=\"required\"");
            StringAssert.Contains(html, "aria-invalid=\"true\"");
            StringAssert.Contains(html, texts["errors.validation.too_short"]);
            Assert.IsFalse(html.Contains("value=\"short\"", StringComparison.Ordinal));
            var fieldId = Regex.Match(html, "<input[^>]*\\sid=\"([^\"]+)\"").Groups[1].Value;
            StringAssert.Contains(html, "for=\"" + fieldId + "\"");
            StringAssert.Contains(html, "aria-describedby=\"" + fieldId + "-error\"");
        }

        [TestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public async Task Form_Toggle_UsesCanonicalMetadataAndNativeBooleanPosting(bool editable, bool isChecked)
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);

            // Shared Boolean form binding.
            var form = new Form<UpdateInstanceModuleDataV1>(fields => fields.Toggle(item => item.IsEnabled, "design.selected"),
                editable ? "/Settings" : null, editable ? "test-antiforgery-token" : null, new UpdateInstanceModuleDataV1(isChecked, 1));
            using var writer = new StringWriter();
            form.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();
            var inputs = Regex.Matches(html, "<input[^>]*name=\"IsEnabled\"[^>]*>").Select(match => match.Value).ToArray();
            Assert.AreEqual(editable ? 2 : 1, inputs.Length);
            StringAssert.Contains(inputs[0], "role=\"switch\"");
            Assert.AreEqual(isChecked, inputs[0].Contains("checked=\"checked\"", StringComparison.Ordinal));
            Assert.AreEqual(!editable, inputs[0].Contains("disabled=\"disabled\"", StringComparison.Ordinal));
            Assert.IsFalse(inputs[0].Contains("required=", StringComparison.Ordinal));
            if (editable)
            {
                StringAssert.Contains(inputs[0], "value=\"true\"");
                StringAssert.Contains(inputs[1], "value=\"false\"");
            }
        }

        [TestMethod]
        [DataRow(3, true, false)]
        [DataRow(1, true, true)]
        [DataRow(2, false, false)]
        public async Task Razor_RolePolicies_SeparateManualMediaAndConfiguration(int role, bool mediaAllowed, bool configurationAllowed)
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (101, 1, 'Policy test')");
            await host.Fixture.RunSqlAsync($"UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = {role} WHERE \"Id\" = 41");
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Email", "a@example.test"), ("Password", "test-password-123")));
            foreach (var path in new[] { "/Admin/Requests", "/Admin/Wanted", "/Admin/Works", "/Admin/Works/101", "/Admin/Library" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(mediaAllowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, page.StatusCode, path);
                if (path == "/Admin/Works" && mediaAllowed)
                {
                    var html = await page.Content.ReadAsStringAsync();
                    StringAssert.Contains(html, "<fieldset class=\"ui-field__control\"><legend>");
                    StringAssert.Contains(html, "class=\"page-table\"");
                    StringAssert.Contains(html, "/build/assets/Assets/Shared/shell.entry-");
                }
                if (path == "/Admin/Requests" && mediaAllowed)
                {
                    var html = await page.Content.ReadAsStringAsync();
                    Assert.AreEqual(configurationAllowed, html.Contains("href=\"/Admin/Rules\"", StringComparison.Ordinal));
                    Assert.AreEqual(configurationAllowed, html.Contains("href=\"/Admin/Requests/Users\"", StringComparison.Ordinal));
                }
            }

            foreach (var path in new[] { "/Admin/Rules", "/Admin/Requests/Users", "/Admin/Storage" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(configurationAllowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, page.StatusCode, path);
            }

            // Existing cookies observe a demotion.
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var demoted = await host.SendAsync(HttpMethod.Get, "/Admin/Wanted");
            Assert.AreEqual(HttpStatusCode.Redirect, demoted.StatusCode);
        }

        [TestMethod]
        public async Task Razor_LoginSettingsLogout_UseServicesAndAntiforgery()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("UPDATE \"Accounts\" SET \"AccountRoleTypeId\" = 2 WHERE \"Id\" = 41");
            using var anonymous = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.Redirect, anonymous.StatusCode);

            using var missingToken = await host.SendAsync(HttpMethod.Post, "/Account/Login", new Dictionary<string, string> { ["Email"] = "a@example.test", ["Password"] = "test-password-123" });
            Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
            using var loginPage = await host.SendAsync(HttpMethod.Get, "/Account/Login?ReturnUrl=https://foreign.example.test");
            var html = await loginPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, loginPage.StatusCode, html);
            StringAssert.Contains(html, "type=\"submit\"");
            Assert.IsFalse(html.Contains("disabled=", StringComparison.Ordinal));

            using var invalidLogin = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(html, ("Email", "a@example.test"), ("Password", "wrong-password")));
            Assert.AreEqual(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);
            var invalidHtml = await invalidLogin.Content.ReadAsStringAsync();
            StringAssert.Contains(invalidHtml, "role=\"alert\"");
            Assert.IsFalse(invalidHtml.Contains("wrong-password", StringComparison.Ordinal));

            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login?ReturnUrl=https://foreign.example.test", Form(invalidHtml, ("Email", "a@example.test"), ("Password", "test-password-123")));
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
            Assert.AreEqual("/", signedIn.Headers.Location!.OriginalString);

            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            html = await settings.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
            StringAssert.Contains(html, "lang=\"de-DE\"");
            StringAssert.Contains(html, "value=\"Profile A\"");
            using var admin = await host.SendAsync(HttpMethod.Get, "/Admin/Requests/Users");
            Assert.AreEqual(HttpStatusCode.Redirect, admin.StatusCode);
            using var denied = await host.SendAsync(HttpMethod.Get, admin.Headers.Location!.PathAndQuery);
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            StringAssert.Contains(await denied.Content.ReadAsStringAsync(), "role=\"alert\"");
            using var invalidLocale = await host.SendAsync(HttpMethod.Post, "/Settings", Form(html, ("DisplayName", "Must roll back"), ("UiLocaleId", "999")));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidLocale.StatusCode);
            Assert.AreEqual("Profile A", await host.Fixture.NameAsync(host.Fixture.ProfileAId));
            var invalidSettings = await invalidLocale.Content.ReadAsStringAsync();
            StringAssert.Contains(invalidSettings, "aria-invalid=\"true\"");

            await host.Fixture.RunSqlAsync("UPDATE \"UiLocales\" SET \"IsEnabled\" = FALSE WHERE \"Id\" = 2");
            using var disabledLocale = await host.SendAsync(HttpMethod.Post, "/Settings", Form(invalidSettings, ("DisplayName", "Must also roll back"), ("UiLocaleId", "2")));
            Assert.AreEqual(HttpStatusCode.BadRequest, disabledLocale.StatusCode);
            Assert.AreEqual("Profile A", await host.Fixture.NameAsync(host.Fixture.ProfileAId));
            await host.Fixture.RunSqlAsync("UPDATE \"UiLocales\" SET \"IsEnabled\" = TRUE WHERE \"Id\" = 2");

            using var saved = await host.SendAsync(HttpMethod.Post, "/Settings", Form(invalidSettings, ("DisplayName", "<b>New name</b>"), ("UiLocaleId", "2")));
            Assert.AreEqual(HttpStatusCode.Redirect, saved.StatusCode);
            using var updated = await host.SendAsync(HttpMethod.Get, "/Settings");
            html = await updated.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "lang=\"en-GB\"");
            StringAssert.Contains(html, "&lt;b&gt;New name&lt;/b&gt;");

            using var sessions = await host.SendAsync(HttpMethod.Get, "/Account/Sessions");
            html = await sessions.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, sessions.StatusCode, html);
            StringAssert.Contains(html, "name=\"SessionPublicId\"");
            using var foreignSession = await host.SendAsync(HttpMethod.Post, "/Account/Sessions", Form(html, ("SessionPublicId", Guid.NewGuid().ToString())));
            Assert.AreEqual(HttpStatusCode.NotFound, foreignSession.StatusCode);
            using var revoked = await host.SendAsync(HttpMethod.Post, "/Account/Sessions", Form(html, ("SessionPublicId", host.Fixture.SessionPublicId.ToString())));
            Assert.AreEqual(HttpStatusCode.Redirect, revoked.StatusCode);

            using var logoutPage = await host.SendAsync(HttpMethod.Get, "/Account/Logout");
            using var loggedOut = await host.SendAsync(HttpMethod.Post, "/Account/Logout", Form(await logoutPage.Content.ReadAsStringAsync()));
            Assert.AreEqual(HttpStatusCode.Redirect, loggedOut.StatusCode);
            using var protectedPage = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.Redirect, protectedPage.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"RevokedAt\" IS NOT NULL"));
        }

        [TestMethod]
        public async Task Razor_ProfileSelection_RotatesSessionAndRejectsPinAndForeignProfiles()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Profiles\" (\"Id\", \"OwnerAccountId\", \"DisplayName\", \"UiLocaleId\") VALUES (303, 41, 'Second profile', 2); INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (41, 303)");
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Email", "a@example.test"), ("Password", "test-password-123")));
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);
            StringAssert.Contains(signedIn.Headers.Location!.OriginalString, "/Account/Profiles");
            var oldCookies = new Dictionary<string, string>(host.Cookies);

            using var picker = await host.SendAsync(HttpMethod.Get, "/Account/Profiles?returnUrl=%2FSettings");
            var html = await picker.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, picker.StatusCode);
            StringAssert.Contains(html, "Second profile");
            Assert.IsFalse(html.Contains("Profile B", StringComparison.Ordinal));
            using var foreign = await host.SendAsync(HttpMethod.Post, "/Account/Profiles", Form(html, ("ProfileId", "202")));
            Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);

            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = 'selection-guard' WHERE \"Id\" = 303");
            using var locked = await host.SendAsync(HttpMethod.Post, "/Account/Profiles", Form(html, ("ProfileId", "303")));
            Assert.AreEqual(HttpStatusCode.Forbidden, locked.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"RotationRevision\" = 1"));
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = NULL WHERE \"Id\" = 303");

            using var selected = await host.SendAsync(HttpMethod.Post, "/Account/Profiles?returnUrl=%2FSettings", Form(html, ("ProfileId", "303")));
            Assert.AreEqual(HttpStatusCode.Redirect, selected.StatusCode);
            Assert.AreEqual("/Settings", selected.Headers.Location!.OriginalString);
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            StringAssert.Contains(await settings.Content.ReadAsStringAsync(), "Second profile");
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"ActiveProfileId\" = 303 AND \"RotationRevision\" = 2"));

            host.Cookies.Clear();
            foreach (var cookie in oldCookies) host.Cookies.Add(cookie.Key, cookie.Value);
            using var stale = await host.SendAsync(HttpMethod.Get, "/Settings");
            Assert.AreEqual(HttpStatusCode.Redirect, stale.StatusCode);
        }

        [TestMethod]
        public async Task Razor_Plex_PublicServices_CompleteApprovalWithoutExposingProof()
        {
            await using var host = await Host.CreateAsync(plexEnabled: true, razor: true);
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            var html = await login.Content.ReadAsStringAsync();
            StringAssert.Contains(html, "Sign in with Plex");
            using var started = await host.SendAsync(HttpMethod.Post, "/Account/Login?handler=Plex", Form(html));
            html = await started.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode);
            StringAssert.Contains(html, "https://app.plex.tv/auth");
            Assert.IsFalse(html.Contains(host.Cookies["jularr.auth_nonce"], StringComparison.Ordinal));
            var id = Regex.Match(html, "name=\"AuthChallengePublicId\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.IsTrue(Guid.TryParse(id, out _));
            using var completed = await host.SendAsync(HttpMethod.Post, "/Account/Login?handler=PlexComplete", Form(html, ("AuthChallengePublicId", id)));
            var approval = await completed.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode);
            StringAssert.Contains(approval, "awaiting administrator approval");
            Assert.IsFalse(approval.Contains("verified-plex-token", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Razor_NonOwner_HidesProfileWritesAndRejectsForgedPost()
        {
            await using var host = await Host.CreateAsync(razor: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"AccountProfiles\" (\"AccountId\", \"ProfileId\") VALUES (42, 101); UPDATE \"Profiles\" SET \"OwnerAccountId\" = 42 WHERE \"Id\" = 101");
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Email", "a@example.test"), ("Password", "test-password-123")));
            using var settings = await host.SendAsync(HttpMethod.Get, "/Settings");
            var html = await settings.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
            Assert.IsFalse(html.Contains("name=\"DisplayName\"", StringComparison.Ordinal));

            using var logoutPage = await host.SendAsync(HttpMethod.Get, "/Account/Logout");
            using var forged = await host.SendAsync(HttpMethod.Post, "/Settings", Form(await logoutPage.Content.ReadAsStringAsync(), ("DisplayName", "Forbidden"), ("UiLocaleId", "2")));
            Assert.AreEqual(HttpStatusCode.Forbidden, forged.StatusCode);
            Assert.AreEqual("Profile A", await host.Fixture.NameAsync(host.Fixture.ProfileAId));
        }

        private static Dictionary<string, string> Form(string html, params (string Name, string Value)[] values)
        {
            var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            Assert.IsTrue(match.Success, "The SSR form must contain an antiforgery token.");
            var form = values.ToDictionary(value => value.Name, value => value.Value);
            form.Add("__RequestVerificationToken", WebUtility.HtmlDecode(match.Groups[1].Value));
            return form;
        }
    }
}
