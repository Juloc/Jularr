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
using Jularr.Backend.Data.Enums.Media;
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
        public async Task MediaCard_ProgressAndStates_RenderAccessibleActions()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);
            var assets = scope.ServiceProvider.GetRequiredService<FrontendAssets>();

            // Standalone symbol stays reusable.
            var availability = new MediaAvailability(assets, MediaAvailabilityState.PartialOtherAudio, 12, 24);
            using var writer = new StringWriter();
            availability.WriteTo(writer, HtmlEncoder.Default, texts);
            var symbol = writer.ToString();
            StringAssert.Contains(symbol, "stroke-dasharray=\"50 100\"");
            Assert.IsFalse(symbol.Contains("<a", StringComparison.Ordinal));

            // Native actions, encoded text.
            writer.GetStringBuilder().Clear();
            var card = new MediaCard(assets, "<Title>", "/Details/1", null).Facts(2026, "media.type.Series", 7.84m).Action("media.continue", "/Player/2")
                .Availability(availability).Continue(30000, 90000);
            card.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();
            StringAssert.Contains(html, "&lt;Title&gt;");
            StringAssert.Contains(html, "media-availability-badge--partialotheraudio");
            StringAssert.Contains(html, "href=\"/Player/2\"");
            StringAssert.Contains(html, "class=\"media-card__title\"");
            StringAssert.Contains(html, "class=\"media-card__details\"");
            StringAssert.Contains(html, "<progress max=\"90000\" value=\"30000\"");
            StringAssert.Contains(html, "<p class=\"media-card__info\" data-full=\"2026 &#xB7; Series &#xB7; " + 7.8m.ToString("0.0", CultureInfo.CurrentCulture) + "\">");
            StringAssert.Contains(html, "class=\"media-card__title\" data-full=\"&lt;Title&gt;\"");
            Assert.IsFalse(html.Contains("data-trailer-id", StringComparison.Ordinal));
            Assert.AreEqual(1, Regex.Matches(html, "media-card__actions").Count);
            foreach (var omitted in new[] { "media-card__series", "media-card__genres", "media-card__languages", "media-card__remaining", "<iframe", "disabled=\"disabled\"" })
                Assert.IsFalse(html.Contains(omitted, StringComparison.Ordinal), omitted);

            // Progression has no invented duration.
            writer.GetStringBuilder().Clear();
            new MediaCard(assets, "Book", "/Details/1", null).Continue(30000, null).WriteTo(writer, HtmlEncoder.Default, texts);
            Assert.IsFalse(writer.ToString().Contains("<progress", StringComparison.Ordinal));
            writer.GetStringBuilder().Clear();
            new MediaCard(assets, "Book", "/Details/1", null).ContinueReading(0.455m).WriteTo(writer, HtmlEncoder.Default, texts);
            StringAssert.Contains(writer.ToString(), "<progress max=\"100\" value=\"46\"");
            Assert.IsFalse(writer.ToString().Contains("media-card__remaining", StringComparison.Ordinal));
            Assert.IsFalse(writer.ToString().Contains("media-card__info", StringComparison.Ordinal));

            // The facts leave out what is unknown; a card to continue reads "Type Year | detail".
            writer.GetStringBuilder().Clear();
            new MediaCard(assets, "Show", "/Details/1", null).Facts(null, "media.type.Series", null).WriteTo(writer, HtmlEncoder.Default, texts);
            StringAssert.Contains(writer.ToString(), "data-full=\"Series\"");
            Assert.IsFalse(writer.ToString().Contains("media-card__rating", StringComparison.Ordinal));
            writer.GetStringBuilder().Clear();
            new MediaCard(assets, "Show", "/Details/1", null).Facts(2023, "media.type.Series", null).Resume("S01 E05 · 22 min left").WriteTo(writer, HtmlEncoder.Default, texts);
            StringAssert.Contains(writer.ToString(), "media-card media-card--resume");
            StringAssert.Contains(writer.ToString(), "data-full=\"Series 2023 | S01 E05 &#xB7; 22 min left\"");

            // The coarse state shows a tag only where the title is held or requested.
            foreach (var (state, tag) in new[] { (MediaStateType.Available, "available"), (MediaStateType.Requested, "requested") })
            {
                writer.GetStringBuilder().Clear();
                new MediaCard(assets, "Show", "/Details/1", null).State(state).WriteTo(writer, HtmlEncoder.Default, texts);
                StringAssert.Contains(writer.ToString(), "media-availability-badge--" + tag);
            }

            writer.GetStringBuilder().Clear();
            new MediaCard(assets, "Show", "/Details/1", null).State(MediaStateType.NotRequested).WriteTo(writer, HtmlEncoder.Default, texts);
            Assert.IsFalse(writer.ToString().Contains("media-availability-badge", StringComparison.Ordinal));

            // Preview keeps real fallback links.
            writer.GetStringBuilder().Clear();
            card.Preview("preview-test").WriteTo(writer, HtmlEncoder.Default, texts);
            Assert.AreEqual(1, Regex.Matches(writer.ToString(), "data-preview-open=\"preview-test\"").Count);
            writer.GetStringBuilder().Clear();
            availability.WriteTo(writer, HtmlEncoder.Default, texts);
            Assert.AreEqual(symbol, writer.ToString());
            writer.GetStringBuilder().Clear();
            new MediaAvailability(assets, MediaAvailabilityState.Missing).Badge("/Details/1").WriteTo(writer, HtmlEncoder.Default, texts);
            Assert.AreEqual("", writer.ToString());

            // Counts and language remain distinct.
            foreach (var state in new[] { MediaAvailabilityState.ProfileAudio, MediaAvailabilityState.OtherAudio, MediaAvailabilityState.Partial, MediaAvailabilityState.PartialOtherAudio })
            {
                writer.GetStringBuilder().Clear();
                new MediaAvailability(assets, state, 12, 24).Badge("/Details/1").WriteTo(writer, HtmlEncoder.Default, texts);
                var label = string.Format(CultureInfo.CurrentCulture, texts["media.availability." + state], 12, 24);
                StringAssert.Contains(writer.ToString(), HtmlEncoder.Default.Encode(label));
            }

            // Inert dialog until user action.
            writer.GetStringBuilder().Clear();
            new MediaPreview("preview-test", assets, "<Title>", null, null, null, "<Overview>").Trailer("ayi6VfCKBcA").WriteTo(writer, HtmlEncoder.Default, texts);
            StringAssert.Contains(writer.ToString(), "&lt;Overview&gt;");
            Assert.IsFalse(writer.ToString().Contains("<iframe", StringComparison.Ordinal));

            // Bookmark uses the canonical POST.
            writer.GetStringBuilder().Clear();
            new MediaBookmark(assets, 1, false, "/Library?type=movies", "test-token").WriteTo(writer, HtmlEncoder.Default, texts);
            html = writer.ToString();
            StringAssert.Contains(html, "method=\"post\"");
            StringAssert.Contains(html, "name=\"__RequestVerificationToken\" value=\"test-token\"");
            StringAssert.Contains(html, "name=\"IsWatchlisted\" value=\"True\"");
            StringAssert.Contains(html, "type=\"submit\"");
            StringAssert.Contains(html, "returnUrl=%2FLibrary%3Ftype%3Dmovies");
            writer.GetStringBuilder().Clear();
            new MediaBookmark(assets, 1).WriteTo(writer, HtmlEncoder.Default, texts);
            StringAssert.Contains(writer.ToString(), "href=\"/Details/1\"");
            Assert.IsFalse(writer.ToString().Contains("<form", StringComparison.Ordinal));
        }

        [TestMethod]
        [DataRow("Development", HttpStatusCode.OK)]
        [DataRow("Production", HttpStatusCode.NotFound)]
        public async Task ApprovedCards_EnvironmentGate_ShowsEightExplicitSamples(string environment, HttpStatusCode expected)
        {
            await using var host = await Host.CreateAsync(razor: true);
            host.Services.GetRequiredService<IHostEnvironment>().EnvironmentName = environment;
            using var response = await host.SendAsync(HttpMethod.Get, "/Design?pane=cards");
            Assert.AreEqual(expected, response.StatusCode);
            if (expected != HttpStatusCode.OK) return;

            var html = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(8, Regex.Matches(html, "data-media-card=\"true\"").Count);
            Assert.AreEqual(8, Regex.Matches(html, "<template id=\"card-preview-").Count);
            Assert.AreEqual(7, Regex.Matches(html, "class=\"media-availability-badge media-availability-badge--").Count);
            foreach (var state in new[] { "profileaudio", "otheraudio", "partial", "partialotheraudio", "downloading", "searching", "requested" })
                StringAssert.Contains(html, "media-availability-badge--" + state);
            Assert.IsFalse(html.Contains("<iframe", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("media-card__languages", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task SignedOut_BackgroundRequestsGet401_PageVisitsGoToSignIn()
        {
            // No cookies: a page visit is sent to sign in, a script's fetch only learns it is signed out.
            await using var host = await Host.CreateAsync(razor: true);
            using var visit = new HttpRequestMessage(HttpMethod.Get, "/Continue/Reading");
            visit.Headers.Add("Sec-Fetch-Mode", "navigate");
            using var visited = await host.Client.SendAsync(visit);
            Assert.AreEqual(HttpStatusCode.Redirect, visited.StatusCode);
            StringAssert.Contains(visited.Headers.Location!.OriginalString, "/Account/Login");
            using var fetch = new HttpRequestMessage(HttpMethod.Get, "/Continue/Reading");
            fetch.Headers.Add("Sec-Fetch-Mode", "cors");
            using var fetched = await host.Client.SendAsync(fetch);
            Assert.AreEqual(HttpStatusCode.Unauthorized, fetched.StatusCode);
            Assert.IsFalse(fetched.Headers.Contains("Set-Cookie"));
        }

        [TestMethod]
        public async Task CardBookmark_Post_RequiresAntiforgeryAndReturnsOnlyLocally()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (101, 1, 'Bookmark test')");
            using var loginPage = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            using var login = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await loginPage.Content.ReadAsStringAsync(), ("Username", "ACCOUNT_A"), ("Password", "test-password-123")));
            Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);

            using var details = await host.SendAsync(HttpMethod.Get, "/Details/101");
            Assert.AreEqual(HttpStatusCode.OK, details.StatusCode);
            var html = await details.Content.ReadAsStringAsync();
            using var withoutToken = new HttpRequestMessage(HttpMethod.Post, "/Details/101")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["IsWatchlisted"] = "true" })
            };
            withoutToken.Headers.Add("Cookie", string.Join("; ", host.Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
            using var missingToken = await host.Client.SendAsync(withoutToken);
            Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);

            using var added = await host.SendAsync(HttpMethod.Post, "/Details/101?returnUrl=%2FLibrary%3Ftype%3Dmovies", Form(html, ("IsWatchlisted", "true")));
            Assert.AreEqual(HttpStatusCode.Redirect, added.StatusCode);
            Assert.AreEqual("/Library?type=movies", added.Headers.Location!.OriginalString);
            using var removed = await host.SendAsync(HttpMethod.Post, "/Details/101?returnUrl=https%3A%2F%2Fforeign.example.test", Form(html, ("IsWatchlisted", "false")));
            Assert.AreEqual(HttpStatusCode.Redirect, removed.StatusCode);
            Assert.AreEqual("/Details/101", removed.Headers.Location!.OriginalString);
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
            Assert.IsFalse(html.Contains("appearance-approved-", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("library-approved-", StringComparison.Ordinal));
            StringAssert.Contains(html, "data-start-url=\"\"");
            StringAssert.Contains(html, "class=\"tag tag--accent\"");
            StringAssert.Contains(html, "role=\"switch\"");
            StringAssert.Contains(html, "class=\"ui-field__label\"");
            StringAssert.Contains(html, "<div class=\"ui-field__top\"><label class=\"ui-field__label\"");
            StringAssert.Contains(html, "class=\"navigation-tabs\"");
            StringAssert.Contains(html, "your-name-poster-");
            StringAssert.Contains(html, "suzume-poster-");
            StringAssert.Contains(html, "data-media-card=\"true\"");
            StringAssert.Contains(html, "media-card__title");
            StringAssert.Contains(html, "media-card__details");
            StringAssert.Contains(html, "data-shelf-direction=\"next\"");
            StringAssert.Contains(html, "class=\"media-shelf__items\" tabindex=\"0\" role=\"region\"");
            foreach (var state in new[] { MediaAvailabilityState.Available, MediaAvailabilityState.Partial, MediaAvailabilityState.Missing, MediaAvailabilityState.Downloading, MediaAvailabilityState.Searching })
                StringAssert.Contains(html, "media-availability--" + state.ToString().ToLowerInvariant());
            Assert.AreEqual(1, Regex.Matches(html, "<dialog\\b").Count);
            // The layout's own template of the missing-cover stand-ins is not a design example.
            Assert.AreEqual(9, Regex.Matches(html, "<template\\b(?![^>]*data-no-cover)").Count);
            StringAssert.Contains(html, "data-preview-open=\"preview-series-Continuing\"");
            // MediaPreview still supports trailers independently of the simplified MediaCard.
            StringAssert.Contains(html, "data-trailer-id=\"k4xGqY5IDBE\"");
            StringAssert.Contains(html, "#icon-close");
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
        [DataRow(999L, "999 B")]
        [DataRow(1_500_000_000L, "1.5 GB")]
        [DataRow(4_000_000_000_000L, "4.0 TB")]
        public void ByteSize_Format_UsesDecimalUnits(long bytes, string expected) =>
            Assert.AreEqual(expected.Replace(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal), ByteSize.Format(bytes));

        [TestMethod]
        public async Task Razor_PageFolders_PreservePublicRoutes()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("INSERT INTO \"Works\" (\"Id\", \"MediaTypeId\", \"CanonicalTitle\") VALUES (101, 1, 'Route test')");
            using var login = await host.SendAsync(HttpMethod.Get, "/Account/Login");
            var loginHtml = await login.Content.ReadAsStringAsync();
            // The sign-in page offers the two round buttons that go to the next design and the next brightness.
            StringAssert.Contains(loginHtml, "data-cycle-skin");
            StringAssert.Contains(loginHtml, "data-cycle-mode");
            Assert.IsFalse(loginHtml.Contains("data-set-skin", StringComparison.Ordinal));
            Assert.IsFalse(loginHtml.Contains("data-accent-input", StringComparison.Ordinal));

            // No sign-in method besides the password is configured: no providers row, and the forgot-password note stays an inline hint.
            Assert.IsFalse(loginHtml.Contains("auth-providers", StringComparison.Ordinal));
            StringAssert.Contains(loginHtml, "id=\"forgot-hint\"");
            StringAssert.Contains(loginHtml, "href=\"#forgot-hint\"");

            // Both skins' marks are rendered and the skin shows one; the browser tab and home screen show the Clean mark.
            Assert.AreEqual(1, Regex.Matches(loginHtml, "brand-symbol__clean").Count);
            Assert.AreEqual(1, Regex.Matches(loginHtml, "brand-symbol__original").Count);
            StringAssert.Contains(loginHtml, "rel=\"icon\"");
            StringAssert.Contains(loginHtml, "rel=\"apple-touch-icon\"");
            foreach (var cssClass in new[] { "brand-symbol__clean", "brand-symbol__original" })
            {
                var markup = Regex.Match(loginHtml, "<svg[^>]*class=\"" + cssClass + "\"[^>]*>.*?</svg>", RegexOptions.Singleline);
                Assert.IsTrue(markup.Success);
                var svg = XElement.Parse(markup.Value);
                Assert.AreEqual("0 0 48 48", svg.Attribute("viewBox")?.Value);
                Assert.AreEqual("48", svg.Element("use")?.Attribute("width")?.Value);
                Assert.AreEqual("48", svg.Element("use")?.Attribute("height")?.Value);
            }

            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(loginHtml, ("Username", "a@example.test"), ("Password", "test-password-123")));
            Assert.AreEqual(HttpStatusCode.Redirect, signedIn.StatusCode);

            // Folder moves preserve URLs.
            foreach (var path in new[] { "/", "/Details/101", "/Favorites", "/Settings", "/Account/Profiles", "/Account/Sessions", "/Account/Logout" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, path);
                if (path == "/Settings")
                {
                    var html = await page.Content.ReadAsStringAsync();
                    StringAssert.Contains(html, "data-set-skin=\"original\"");
                    StringAssert.Contains(html, "data-set-mode=\"system\"");
                }

                // The shell names the person by the profile, never by the account's username (which the sign-in page took: account_a).
                if (path == "/")
                {
                    var html = await page.Content.ReadAsStringAsync();
                    StringAssert.Contains(html, ">PA<");
                    Assert.IsFalse(html.Contains("account_a", StringComparison.OrdinalIgnoreCase));
                }
            }

            // The watchlist is Favorites now; old links keep their choices.
            using var watchlist = await host.SendAsync(HttpMethod.Get, "/Watchlist?type=movies&sort=Title");
            Assert.AreEqual(HttpStatusCode.MovedPermanently, watchlist.StatusCode);
            Assert.AreEqual("/Favorites?type=movies&sort=Title", watchlist.Headers.Location?.OriginalString);

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
            var data = new RegisterDataV1("new_user", "short", "short", 1);
            var validation = Assert.ThrowsExactly<DataValidationException>(data.Validate);
            Assert.AreEqual("Password", validation.Field);
            Assert.AreEqual("too_short", validation.Code);
            new LoginDataV1("a@example.test", "short").Validate();
            new RegisterDataV1("new_user", "  valid passphrase  ", "  valid passphrase  ", 1).Validate();
            Assert.AreEqual("password_mismatch", Assert.ThrowsExactly<DataValidationException>(() => new RegisterDataV1("new_user", "valid passphrase one", "valid passphrase two", 1).Validate()).Code);
            Assert.ThrowsExactly<DataValidationException>(() => new RegisterDataV1("new_user", new string('x', 129), new string('x', 129), 1).Validate());

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
        [DataRow(false)]
        [DataRow(true)]
        public async Task Form_Color_ReusesTypedValueAndExistingWriteGuard(bool editable)
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);
            var form = new Form<Jularr.Frontend.Modules.Reader.ReaderSettingsData>(fields => fields.Color(item => item.Colour, "reader.colour"),
                editable ? "/Settings" : null, editable ? "test-antiforgery-token" : null, new Jularr.Frontend.Modules.Reader.ReaderSettingsData());
            using var writer = new StringWriter();
            form.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();
            StringAssert.Contains(html, "name=\"Colour\" type=\"color\"");
            StringAssert.Contains(html, "value=\"#f7f3ec\"");
            StringAssert.Contains(html, "minlength=\"7\"");
            Assert.AreEqual(!editable, html.Contains("disabled=\"disabled\"", StringComparison.Ordinal));
        }

        [TestMethod]
        [DataRow("en-US", "server")]
        [DataRow("de-DE", "server")]
        [DataRow("en-US", "local")]
        [DataRow("de-DE", "local")]
        [DataRow("en-US", "readonly")]
        public async Task Form_Range_PreservesNumericMetadataBindingAndWriteGuard(string locale, string mode)
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(locale);
                var form = new Form<RangeData>(fields => fields.Range(item => item.Fraction, "reader.spacing"),
                    mode == "server" ? "/Settings" : null, mode == "server" ? "token" : null, new RangeData(), local: mode == "local");
                using var writer = new StringWriter();
                form.WriteTo(writer, HtmlEncoder.Default, texts);
                var html = writer.ToString();
                var input = Regex.Match(html, "<input[^>]*name=\"Fraction\"[^>]*>").Value;
                StringAssert.Contains(input, "type=\"range\"");
                StringAssert.Contains(input, "step=\"0.01\"");
                StringAssert.Contains(input, "data-range-precision=\"2\"");
                StringAssert.Contains(input, "value=\"1.25\"");
                StringAssert.Contains(html, "min=\"0.25\" max=\"2.75\"");
                Assert.AreEqual(mode == "readonly", html.Contains("disabled=\"disabled\"", StringComparison.Ordinal));
                Assert.AreEqual(mode == "server", html.Contains("name=\"__Invariant\" value=\"Fraction\"", StringComparison.Ordinal));
                Assert.AreEqual(mode == "local", html.StartsWith("<div", StringComparison.Ordinal));
                var id = Regex.Match(html, "<input id=\"([^\"]+)\" name=\"Fraction\"").Groups[1].Value;
                Assert.IsFalse(string.IsNullOrEmpty(id));
                StringAssert.Contains(html, "for=\"" + id + "\"");
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        [TestMethod]
        public async Task Form_Range_PreservesIndexedNamesAndFieldErrors()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);
            var form = new Form<RangeRows>(fields => fields.Rows(item => item.Items, (row, _) => row.Range(item => item.Fraction, "reader.spacing")),
                "/Settings", "token", new RangeRows(), ServiceError.FromException(new DataValidationException("Items[0].Fraction", "out_of_range")));
            using var writer = new StringWriter();
            form.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();
            StringAssert.Contains(html, "name=\"Items[0].Fraction\" type=\"range\"");
            StringAssert.Contains(html, "name=\"__Invariant\" value=\"Items[0].Fraction\"");
            StringAssert.Contains(html, "aria-invalid=\"true\"");
            StringAssert.Contains(html, "-Items[0].Fraction-error\"");
            StringAssert.Contains(html, "role=\"alert\"");
            Assert.ThrowsExactly<InvalidOperationException>(() => new Form<LoginDataV1>(fields => fields.Range(item => item.Password, "account.field.password"), local: true));
        }

        [TestMethod]
        public async Task Form_Affix_PutsIconAndUnitInsideTheFieldAndKeepsItsMetadata()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);
            var form = new Form<RangeData>(fields => fields.Number(item => item.Fraction, "reader.spacing", affix: FieldAffix.Unit("unit.megabytes")), "/Settings", "token", new RangeData());
            using var writer = new StringWriter();
            form.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();

            // The unit follows the number inside the frame, before its plus and minus; bounds, step and invariant binding stay.
            StringAssert.Contains(html, "ui-field__control ui-field__control--number ui-field__control--affixed");
            Assert.IsTrue(Regex.IsMatch(html, "<input[^>]*name=\"Fraction\"[^>]*min=\"0.25\" max=\"2.75\"[^>]*>\\s*<span class=\"ui-field__suffix\">MB</span><div class=\"ui-field__stepper\""), html);
            StringAssert.Contains(html, "name=\"__Invariant\" value=\"Fraction\"");

            // An icon leads a text field and is hidden from assistive technology; no unit, no suffix.
            var text = new Form<LoginDataV1>(fields => fields.Text(item => item.Username, "account.field.username", affix: new FieldAffix(new Span(new ValueText("CC")))), "/Settings", "token");
            using var other = new StringWriter();
            text.WriteTo(other, HtmlEncoder.Default, texts);
            StringAssert.Contains(other.ToString(), "<span class=\"ui-field__prefix\" aria-hidden=\"true\"><span>CC</span></span><input");
            Assert.IsFalse(other.ToString().Contains("ui-field__suffix", StringComparison.Ordinal));
        }

        private sealed class RangeData : BaseData
        {
            [DataField(typeof(RangeFields), nameof(RangeFields.Fraction))]
            public decimal Fraction { get; } = 1.25m;
        }

        private sealed class RangeRows : BaseData
        {
            public BaseDataList<RangeData> Items { get; } = new([new RangeData()]);
        }

        private static class RangeFields
        {
            public static readonly Jularr.Backend.Data.Fields.DecimalField Fraction = new() { Minimum = .25m, Maximum = 2.75m, Scale = 2 };
        }

        [TestMethod]
        public async Task Form_Local_ReusesMetadataWithoutSubmissionOrServerActions()
        {
            await using var host = await Host.CreateAsync(razor: true);
            using var scope = host.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
            var texts = scope.ServiceProvider.GetRequiredService<UiText>();
            await texts.InitializeAsync(http);
            var form = new Form<Jularr.Frontend.Modules.Reader.ReaderSettingsData>(fields => fields
                .Color(item => item.Colour, "reader.colour").Number(item => item.Size, "reader.sizePixels"),
                data: new Jularr.Frontend.Modules.Reader.ReaderSettingsData(), local: true);
            using var writer = new StringWriter();
            form.WriteTo(writer, HtmlEncoder.Default, texts);
            var html = writer.ToString();
            StringAssert.StartsWith(html, "<div");
            StringAssert.Contains(html, "data-form-local=\"\"");
            StringAssert.Contains(html, "min=\"14\" max=\"40\"");
            Assert.IsFalse(html.Contains("disabled=", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("<form", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("__RequestVerificationToken", StringComparison.Ordinal));
            Assert.ThrowsExactly<ArgumentException>(() => new Form<LoginDataV1>(_ => { }, "/Account/Login", "token", local: true));
            Assert.ThrowsExactly<InvalidOperationException>(() => new Form<LoginDataV1>(fields => fields.Submit("account.login"), local: true));
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
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Username", "a@example.test"), ("Password", "test-password-123")));
            foreach (var path in new[] { "/Admin/Requests", "/Admin/Wanted", "/Admin/Works", "/Admin/Works/101", "/Admin/Library" })
            {
                using var page = await host.SendAsync(HttpMethod.Get, path);
                Assert.AreEqual(mediaAllowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, page.StatusCode, path);
                if (path == "/Admin/Works" && mediaAllowed)
                {
                    var html = await page.Content.ReadAsStringAsync();
                    StringAssert.Contains(html, "<div class=\"ui-field__top\"><label class=\"ui-field__label\"");
                    StringAssert.Contains(html, "data-work-table=\"works\"");
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

            using var missingToken = await host.SendAsync(HttpMethod.Post, "/Account/Login", new Dictionary<string, string> { ["Username"] = "a@example.test", ["Password"] = "test-password-123" });
            Assert.AreEqual(HttpStatusCode.BadRequest, missingToken.StatusCode);
            using var loginPage = await host.SendAsync(HttpMethod.Get, "/Account/Login?ReturnUrl=https://foreign.example.test");
            var html = await loginPage.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, loginPage.StatusCode, html);
            StringAssert.Contains(html, "type=\"submit\"");
            Assert.IsFalse(html.Contains("disabled=", StringComparison.Ordinal));

            using var invalidLogin = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(html, ("Username", "a@example.test"), ("Password", "wrong-password")));
            Assert.AreEqual(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);
            var invalidHtml = await invalidLogin.Content.ReadAsStringAsync();
            StringAssert.Contains(invalidHtml, "role=\"alert\"");
            Assert.IsFalse(invalidHtml.Contains("wrong-password", StringComparison.Ordinal));

            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login?ReturnUrl=https://foreign.example.test", Form(invalidHtml, ("Username", "a@example.test"), ("Password", "test-password-123")));
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
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Username", "a@example.test"), ("Password", "test-password-123")));
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

            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = 'selection-guard', \"PinLength\" = 4 WHERE \"Id\" = 303");
            using var locked = await host.SendAsync(HttpMethod.Post, "/Account/Profiles", Form(html, ("ProfileId", "303")));
            Assert.AreEqual(HttpStatusCode.Forbidden, locked.StatusCode);
            Assert.AreEqual(2L, await host.ScalarAsync("SELECT count(*) FROM \"AccountSessions\" WHERE \"RotationRevision\" = 1"));
            await host.Fixture.RunSqlAsync("UPDATE \"Profiles\" SET \"PinHash\" = NULL, \"PinLength\" = NULL WHERE \"Id\" = 303");

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
            StringAssert.Contains(html, "auth-providers");
            StringAssert.Contains(html, "Or continue with");
            Assert.IsFalse(html.Contains("Trakt", StringComparison.Ordinal));
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
            using var signedIn = await host.SendAsync(HttpMethod.Post, "/Account/Login", Form(await login.Content.ReadAsStringAsync(), ("Username", "a@example.test"), ("Password", "test-password-123")));
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
