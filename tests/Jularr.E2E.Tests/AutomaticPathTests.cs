using System.Diagnostics;
using Microsoft.Playwright;

namespace Jularr.E2E.Tests
{
    // Titles nobody searches by hand: Jularr finds them by its profiles, downloads, imports and plays them.
    [TestClass]
    public sealed class AutomaticPathTests
    {
        private const string Username = "viewer_e2e";
        private const string Password = "E2e-Viewer-Password-123!";

        [TestMethod]
        [Timeout(900_000)]
        public async Task SonarrTakeover_ExplicitlyResumedMonitoring_ChoosesReleasesAndEpisodesPlay()
        {
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            using var folders = new Folders("Series");
            var video = await Steps.WriteVideoAsync(ffmpeg, Path.Combine(folders.Fixtures, "video.webm"));

            // The taken-over profile allows only web 1080p and rejects x265 by its custom format.
            var television = new FakeRelease("Jularr.Test.Series.S01E01.720p.HDTV.x264-JLR", "e01-hdtv", 5040, video);
            var x265 = new FakeRelease("Jularr.Test.Series.S01E01.1080p.WEB-DL.x265-JLR", "e01-x265", 5040, video);
            var first = new FakeRelease("Jularr.Test.Series.S01E01.1080p.WEB-DL.H264-JLR", "e01", 5040, video);
            var second = new FakeRelease("Jularr.Test.Series.S01E02.1080p.WEB-DL.H264-JLR", "e02", 5040, video);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg ?? "",
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg) ?? ""
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Series", "Series"));
                await Steps.AddUsenetAsync(page, fakes, "5000,5040", searchesAutomatically: true);

                // The preview assigns Sonarr's root folder to the only series library; taking over brings the profile and the missing series.
                fakes.Offer(television, x265, first, second);
                await TakeOverAsync(page, fakes, apply: false);
                Assert.AreEqual(1, await page.Locator("[name=RootFolders]:checked").CountAsync(), "The root folder was not suggested for the series library.");
                await TakeOverAsync(page, fakes, apply: true);
                await Assertions.Expect(page.Locator("main table").Last).ToContainTextAsync("Wanted");

                // Inventory takeover stays paused across restart until the Owner explicitly takes over automatic acquisition.
                await page.GotoAsync("about:blank");
                await jularr.RestartAsync();
                await page.GotoAsync("/Admin/Wanted");
                var monitored = Steps.ListRow(page, FakeServices.SeriesTitle);
                await Assertions.Expect(monitored).ToContainTextAsync("Paused");
                Assert.AreEqual(0, fakes.Grabbed.Count, "Taking over inventory or restarting must not start downloads.");
                await Steps.SubmitAsync(page, monitored.Locator("form").Filter(new()
                {
                    Has = page.GetByRole(AriaRole.Button, new() { Name = "Resume " + FakeServices.SeriesTitle, Exact = true })
                }));

                // After explicit release, automatic search takes only what the imported profile allows.
                await WaitForGrabsAsync(fakes, first, second);
                var details = await Steps.WaitForAvailableAsync(page, FakeServices.SeriesTitle);
                CollectionAssert.AreEquivalent(new[] { fakes.NzbAddress(first).ToString(), fakes.NzbAddress(second).ToString() }, fakes.Grabbed.ToArray());
                Assert.AreEqual(2, Directory.EnumerateFiles(Path.Combine(folders.Library, "Series"), "*.webm", SearchOption.AllDirectories).Count(), "The episodes were not imported.");

                if (ffmpeg is null)
                {
                    JularrInstance.Unavailable("JULARR_TEST_FFMPEG is not configured; playback was not checked.");
                }

                await page.GotoAsync(details);
                await Steps.PlayAndPauseAsync(page, page.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "automatic-sonarr");
                throw;
            }
        }

        // Priority only decides who is searched first: the bulk bar marks the series High, and one search of both takes its episode before the film.
        [TestMethod]
        [Timeout(900_000)]
        public async Task WantedPriority_TheBulkBarMarksATitleHigh_AndTheSearchTakesItFirst()
        {
            using var folders = new Folders("Movies", "Series");
            var video = await Steps.WriteVideoAsync(null, Path.Combine(folders.Fixtures, "video.webm"));
            var film = new FakeRelease("Big.Buck.Bunny.2008.1080p.WEB-DL.H264-JLR", "bunny", 2040, video);
            var episode = new FakeRelease("Jularr.Test.Series.S01E01.1080p.WEB-DL.H264-JLR", "s01e01", 5040, video);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var session = await BrowserSession.StartAsync(jularr);
            var page = session.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"), new LibraryPath("Series", "Series"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040,5000,5040", searchesAutomatically: true);

                // Nothing is on offer yet: both searches find nothing and the titles wait for their next time.
                await Steps.RequestAsync(page, "Bunny", FakeServices.MovieTitle);
                await Steps.RequestAsync(page, "Test Series", FakeServices.SeriesTitle);
                await page.GotoAsync("/Admin/Wanted");
                var filmRow = Steps.ListRow(page, FakeServices.MovieTitle);
                var seriesRow = Steps.ListRow(page, FakeServices.SeriesTitle);
                await Assertions.Expect(filmRow).ToBeVisibleAsync();
                await Assertions.Expect(seriesRow).Not.ToContainTextAsync("High");

                // Priorities are for chosen rows: the dock shows its bulk zone, with how many are chosen, only while rows are chosen.
                var dock = page.Locator(".action-dock");
                await Assertions.Expect(dock.GetByRole(AriaRole.Button, new() { Name = "High priority", Exact = true })).ToBeHiddenAsync();
                await seriesRow.GetByRole(AriaRole.Checkbox, new() { Name = "Select" }).CheckAsync();
                await Assertions.Expect(dock).ToContainTextAsync("1 selected");
                await Evidence.SaveAsync(page, "wanted-bulk");
                await Steps.FollowAsync(page, dock.GetByRole(AriaRole.Button, new() { Name = "High priority", Exact = true }));
                await Assertions.Expect(seriesRow).ToContainTextAsync("High");
                await Assertions.Expect(filmRow).Not.ToContainTextAsync("High");

                // One search of both: the high title is searched first, so its release is taken first.
                fakes.Offer(film, episode);
                await page.Locator(".list-head").GetByRole(AriaRole.Checkbox, new() { Name = "Select all" }).CheckAsync();
                await Steps.FollowAsync(page, dock.GetByRole(AriaRole.Button, new() { Name = "Search selected", Exact = true }));
                await Assertions.Expect(page.Locator("main [role=status]")).ToContainTextAsync("The search has started.");
                await WaitForGrabsAsync(fakes, episode, film);
                CollectionAssert.AreEqual(new[] { fakes.NzbAddress(episode).ToString(), fakes.NzbAddress(film).ToString() }, fakes.Grabbed.ToArray());
                CollectionAssert.AreEqual(Array.Empty<string>(), session.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "automatic-priority");
                throw;
            }
        }

        [TestMethod]
        [Timeout(900_000)]
        public async Task UserRequests_FilmByACustomProfileAndAnAnime_AreFoundAutomatically_AndPlay()
        {
            var ffmpeg = Environment.GetEnvironmentVariable("JULARR_TEST_FFMPEG");
            using var folders = new Folders("Movies", "Anime");
            var video = await Steps.WriteVideoAsync(ffmpeg, Path.Combine(folders.Fixtures, "video.webm"));

            // The better English Blu-ray loses against the custom profile that wants German; the anime comes in its usual release names.
            var english = new FakeRelease("Big.Buck.Bunny.2008.1080p.BluRay.x264-JLR", "bunny-en", 2040, video);
            var german = new FakeRelease("Big.Buck.Bunny.2008.German.DL.1080p.WEB-DL.H264-JLR", "bunny-de", 2040, video);
            var opening = new FakeRelease("[JLR] Example Anime - 01 [1080p][WEB-DL]", "anime-01", 5070, video);
            var ending = new FakeRelease("[JLR] Example Anime - 02 [1080p][WEB-DL]", "anime-02", 5070, video);
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:AniList"] = fakes.AniList.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library,
                ["Media:FfmpegPath"] = ffmpeg ?? "",
                ["Media:FfprobePath"] = Steps.FfprobeBeside(ffmpeg) ?? ""
            });
            await using var owner = await BrowserSession.StartAsync(jularr);
            await using var viewer = await BrowserSession.StartAsync(jularr);
            var page = owner.Page;
            var user = viewer.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.SetTmdbKeyAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Movies", "Movie"), new LibraryPath("Anime", "Series", "Anime"));
                await Steps.AddUsenetAsync(page, fakes, "2000,2040,5000,5070", searchesAutomatically: true);
                await CreateProfileAsync(page, "Movie", "German", mustContain: "German");
                await RegisterAsync(page, user);

                // The user asks for both; the Owner approves them; nobody searches by hand.
                fakes.Offer(english, german, opening, ending);
                await Steps.RequestAsync(user, "Bunny", FakeServices.MovieTitle);
                await Steps.RequestFromCatalogAsync(user, "anime", FakeServices.AnimeTitle, "anilist");
                await ApproveAsync(page, FakeServices.MovieTitle);
                await ApproveAsync(page, FakeServices.AnimeTitle);
                await WaitForGrabsAsync(fakes, german, opening, ending);
                CollectionAssert.DoesNotContain(fakes.Grabbed.ToArray(), fakes.NzbAddress(english).ToString(), "The custom profile's German release was not preferred.");

                // Both arrive for the user and play.
                var film = await Steps.WaitForAvailableAsync(user, FakeServices.MovieTitle);
                var anime = await Steps.WaitForAvailableAsync(user, FakeServices.AnimeTitle);
                Assert.AreEqual(2, Directory.EnumerateFiles(Path.Combine(folders.Library, "Anime"), "*.webm", SearchOption.AllDirectories).Count(), "The anime episodes were not imported.");
                await user.GotoAsync("/Requests");
                await Assertions.Expect(user.Locator(".request-row", new() { HasText = FakeServices.MovieTitle })).ToContainTextAsync("Available");

                if (ffmpeg is null)
                {
                    JularrInstance.Unavailable("JULARR_TEST_FFMPEG is not configured; playback was not checked.");
                }

                await user.GotoAsync(film);
                await Steps.PlayAndPauseAsync(user, user.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);
                await user.GotoAsync(anime);
                await Steps.PlayAndPauseAsync(user, user.GetByRole(AriaRole.Link, new() { Name = "Play", Exact = true }).First);
                CollectionAssert.AreEqual(Array.Empty<string>(), owner.Problems.ToArray());
                CollectionAssert.AreEqual(Array.Empty<string>(), viewer.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "automatic-owner");
                await Evidence.SaveAsync(user, "automatic-user");
                throw;
            }
        }

        [TestMethod]
        [Timeout(900_000)]
        public async Task UserRequestsABook_ThePdfIsFoundAutomatically_AndReadsWithItsPicture()
        {
            using var folders = new Folders("Books");
            string[] Text(int paragraphs) => [.. Enumerable.Range(1, paragraphs).SelectMany(number => new[] { "Paragraph " + number + " runs across the page like the text of any printed book",
                "does, line after line, until it ends here.", "" })];
            var pdf = new FakeRelease("Jane Austen - Pride and Prejudice (pdf)", "pride-pdf", 7020, Steps.WritePdfWithPicture(Path.Combine(folders.Fixtures, "book.pdf"), 0,
                ["Chapter One", "", "", "It is a truth universally acknowledged, that a single man in possession", "of a good fortune, must be in want of a wife.", "", .. Text(8)],
                [.. Text(16)],
                ["Chapter Two", "", "", "Mr. Bennet was among the earliest of those who waited on Mr. Bingley.", "", .. Text(13)]));
            await using var fakes = await FakeServices.StartAsync(folders.Downloads);
            await using var jularr = await JularrInstance.StartAsync(new Dictionary<string, string>
            {
                ["Providers:Tmdb"] = fakes.Tmdb3.ToString(),
                ["Providers:Images:image.tmdb.org"] = fakes.Images.ToString(),
                ["Providers:OpenLibrary"] = fakes.OpenLibrary.ToString(),
                ["Providers:Images:covers.openlibrary.org"] = fakes.Images.ToString(),
                ["Storage:LocalPaths:0"] = folders.Library
            });
            await using var owner = await BrowserSession.StartAsync(jularr);
            await using var viewer = await BrowserSession.StartAsync(jularr);
            var page = owner.Page;
            var user = viewer.Page;

            try
            {
                await Owner.SetUpAndSignInAsync(page);
                await Steps.AddLibraryAsync(page, folders.Library, new LibraryPath("Books", "Book"));
                await Steps.AddUsenetAsync(page, fakes, "7000,7020", searchesAutomatically: true);
                await RegisterAsync(page, user);

                // The user asks for the novel; once approved, nobody searches by hand.
                fakes.Offer(pdf);
                await Steps.RequestFromCatalogAsync(user, "books", FakeServices.BookTitle, "openlibrary");
                await ApproveAsync(page, FakeServices.BookTitle);
                await WaitForGrabsAsync(fakes, pdf);

                // The PDF reads reflowed, with its picture where it stood.
                var details = await Steps.WaitForAvailableAsync(user, FakeServices.BookTitle, "Read");
                await Steps.OpenReaderAsync(user, details);
                var content = user.Locator("[data-role=content]");
                await Assertions.Expect(content).ToContainTextAsync("must be in want of a wife.");
                var picture = content.Locator("img").First;
                await Assertions.Expect(picture).ToBeVisibleAsync(new() { Timeout = Steps.MediaTimeout });
                Assert.IsTrue(await picture.EvaluateAsync<bool>("image => image.complete && image.naturalWidth > 0"), "The PDF's picture did not load.");
                CollectionAssert.AreEqual(Array.Empty<string>(), owner.Problems.ToArray());
                CollectionAssert.AreEqual(Array.Empty<string>(), viewer.Problems.ToArray());
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                await Evidence.SaveAsync(page, "automatic-book-owner");
                await Evidence.SaveAsync(user, "automatic-book-user");
                throw;
            }
        }

        // Reads Sonarr once; the key is entered again for taking over.
        private static async Task TakeOverAsync(IPage page, FakeServices fakes, bool apply)
        {
            if (!apply)
            {
                await page.GotoAsync("/Admin/Acquisition/Takeover");
            }

            var form = page.Locator("main form", new() { Has = page.Locator("[name=TakeoverSourceTypeId]") });
            await form.Locator("[name=TakeoverSourceTypeId]").SelectOptionAsync(new SelectOptionValue { Label = "Sonarr (indexers, client, missing series)" });
            await form.Locator("[name=BaseUrl]").FillAsync(fakes.Sonarr.ToString());
            await form.Locator("[name=ApiKey]").FillAsync(FakeServices.SonarrKey);
            await form.Locator("[name=Apply][value=" + (apply ? "True" : "False") + "]").CheckAsync();
            await Steps.SubmitAsync(page, form);
        }

        // A profile requires its scored format, made the media type's default.
        private static async Task CreateProfileAsync(IPage page, string type, string name, string mustContain)
        {
            await page.GotoAsync("/Admin/Acquisition/Profiles?tab=formats&mediaTypeId=1&new=true");
            var format = page.Locator("dialog#format-editor form").First;
            await format.Locator("[name=Name]").FillAsync(name);
            await format.Locator("[name='Conditions[0].Name']").FillAsync(name);
            await format.Locator("[name='Conditions[0].Value']").FillAsync(mustContain);
            await Steps.SubmitAsync(page, format);
            await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
            await page.GotoAsync("/Admin/Acquisition/Profiles/New?type=" + type);
            var form = page.Locator("main form", new() { Has = page.Locator("[name=MinimumFormatScore]") });
            await form.Locator("[name=Name]").FillAsync(name);
            await form.GetByText("Default for its media type", new() { Exact = true }).ClickAsync();
            await Assertions.Expect(form.GetByRole(AriaRole.Switch, new() { Name = "Default for its media type", Exact = true })).ToBeCheckedAsync();
            await form.Locator(".ui-form__row", new() { HasText = name }).Locator("input[name$='.Score']").FillAsync("100");
            await form.Locator("[name=MinimumFormatScore]").FillAsync("100");
            await Steps.SaveAsync(page);
            await Assertions.Expect(page.Locator("main [role=alert]")).ToHaveCountAsync(0);
        }

        // A visitor asks for an account and the Owner enables it; the user signs in.
        private static async Task RegisterAsync(IPage page, IPage user)
        {
            await Steps.SetModuleAsync(page, "Let visitors ask for an account", true);
            await user.GotoAsync("/Account/Register");
            var form = Steps.FormWith(user, "Create account & request access");
            await form.Locator("[name=Username]").FillAsync(Username);
            await form.Locator("[name=Password]").FillAsync(Password);
            await form.Locator("[name=PasswordConfirmation]").FillAsync(Password);
            await Steps.SubmitAsync(user, form);
            await page.GotoAsync("/Admin/Accounts");
            await Steps.FollowAsync(page, page.Locator("tr", new() { HasText = Username }).GetByRole(AriaRole.Link, new() { Name = "Edit" }));
            var account = page.Locator("main form", new() { Has = page.Locator("[name=IsEnabled]") });
            await account.Locator("[name=IsEnabled]").SelectOptionAsync("True");
            await Steps.SubmitAsync(page, account);
            await user.GotoAsync("/Account/Login");
            await user.FillAsync("[name=Username]", Username);
            await user.FillAsync("[name=Password]", Password);
            await user.Locator("form button[type=submit]").First.ClickAsync();
            await user.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
        }

        private static async Task ApproveAsync(IPage page, string title)
        {
            await page.GotoAsync("/Admin/Requests");
            await Steps.SubmitAsync(page, Steps.ListRow(page, title).Locator("form")
                .Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Approve" }) }));
        }

        // The automatic search runs every two minutes.
        private static async Task WaitForGrabsAsync(FakeServices fakes, params FakeRelease[] releases)
        {
            var wanted = releases.Select(release => fakes.NzbAddress(release).ToString()).ToArray();
            var waited = Stopwatch.StartNew();
            while (!wanted.All(fakes.Grabbed.Contains))
            {
                if (waited.Elapsed > TimeSpan.FromMinutes(6))
                {
                    throw new AssertFailedException("Not grabbed automatically within six minutes: " + string.Join(", ", wanted.Where(address => !fakes.Grabbed.Contains(address)))
                        + "; grabbed: " + string.Join(", ", fakes.Grabbed));
                }

                await Task.Delay(3000);
            }
        }
    }
}
