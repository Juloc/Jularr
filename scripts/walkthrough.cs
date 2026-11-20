#:package Microsoft.Playwright@1.64.1
#:property PublishAot=false
// The fixed walkthrough before a PR: signs in on a running local Jularr, opens the main pages at 375, 820 and 1440 px, saves a
// full-page screenshot of each and reports console errors, failed requests, sideways page scroll and load time. Run by
// scripts/walkthrough.sh; never in CI. Usage: dotnet run scripts/walkthrough.cs -- <address> <owner.txt> <output folder>
using System.Diagnostics;
using System.Text;
using Microsoft.Playwright;

var address = args.Length > 0 ? args[0].TrimEnd('/') : "http://127.0.0.1:8190";
var ownerFile = args.Length > 1 ? args[1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/jularr-test/instances/main/owner.txt");
var output = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "jularr-walkthrough");
Directory.CreateDirectory(output);

// owner.txt holds "name=..." and "password=..." lines; the values are never printed.
var owner = File.ReadAllLines(ownerFile).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
var username = owner.FirstOrDefault(p => p.Key != "password").Value ?? throw new InvalidOperationException("owner.txt has no name line");
var password = owner.GetValueOrDefault("password") ?? throw new InvalidOperationException("owner.txt has no password line");

var sizes = new (string Name, int Width, int Height)[] { ("phone", 375, 812), ("tablet", 820, 1180), ("desktop", 1440, 900) };
var results = new List<Result>();
var urls = new Dictionary<string, string?>();

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });

foreach (var size in sizes)
{
    await using var context = await browser.NewContextAsync(new()
    {
        ViewportSize = new() { Width = size.Width, Height = size.Height },
        HasTouch = size.Width < 768,
        Locale = "en-US"
    });
    var page = await context.NewPageAsync();
    Result? current = null;
    // The load time runs from the last navigation (or the sign-in click) to the load event.
    var navigated = Stopwatch.GetTimestamp();
    page.Console += (_, m) => { if (m.Type == "error") current?.Console.Add(Short(m.Text)); };
    page.PageError += (_, e) => current?.Console.Add(Short("page error: " + e));
    page.RequestFailed += (_, r) => { if (r.Failure is { } f && !f.Contains("ERR_ABORTED")) current?.Requests.Add(Short($"{f} {Rel(r.Url)}")); };
    page.Response += (_, r) => { if (r.Status >= 400) current?.Requests.Add(Short($"{r.Status} {Rel(r.Url)}")); };

    async Task Visit(string name, Func<Task<string?>> open)
    {
        current = new Result(name, size.Name, size.Width);
        results.Add(current);
        try
        {
            var path = await open();
            if (path is null) { current.Note = "skipped: nothing to open"; return; }
            await page.WaitForLoadStateAsync(LoadState.Load);
            current.LoadMs = (int)Stopwatch.GetElapsedTime(navigated).TotalMilliseconds;
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5_000 }); } catch (TimeoutException) { current.Note = "network still busy after 5 s"; }
            current.Url = Rel(page.Url);
            current.ScrollWidth = await page.EvaluateAsync<int>("() => Math.max(document.documentElement.scrollWidth, document.body ? document.body.scrollWidth : 0)");
            await page.ScreenshotAsync(new() { Path = System.IO.Path.Combine(output, $"{size.Name}-{size.Width}-{name}.png"), FullPage = true });
        }
        catch (Exception e)
        {
            current.Note = Short("failed: " + e.Message.Split('\n')[0]);
            try { await page.ScreenshotAsync(new() { Path = System.IO.Path.Combine(output, $"{size.Name}-{size.Width}-{name}-failed.png") }); } catch (PlaywrightException) { }
        }
    }

    async Task<string?> Go(string path) { navigated = Stopwatch.GetTimestamp(); await page.GotoAsync(address + path); return path; }
    // Finds a link once (at the first size), on the first page that has one, and reuses it for the other sizes, so every size shows the same thing.
    async Task<string?> Find(string key, string selector, params string[] from)
    {
        if (!urls.TryGetValue(key, out var found))
        {
            foreach (var path in from)
            {
                await page.GotoAsync(address + path);
                var link = page.Locator(selector).First;
                try { await link.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 8_000 }); } catch (TimeoutException) { continue; }
                found = await link.GetAttributeAsync("href");
                break;
            }
            urls[key] = found;
        }
        return found is null ? null : await Go(found);
    }

    await Visit("login", async () =>
    {
        await Go("/Account/Login");
        await page.WaitForLoadStateAsync(LoadState.Load);
        await page.ScreenshotAsync(new() { Path = System.IO.Path.Combine(output, $"{size.Name}-{size.Width}-login-form.png"), FullPage = true });
        await page.FillAsync("[name=Username]", username);
        await page.FillAsync("[name=Password]", password);
        navigated = Stopwatch.GetTimestamp();
        await page.Locator("form button[type=submit]").First.ClickAsync();
        await page.WaitForURLAsync(u => !u.Contains("/Account/Login", StringComparison.Ordinal));
        // A profile picker after sign-in: take the first profile.
        if (page.Url.Contains("/Account/Profiles", StringComparison.Ordinal))
        {
            await page.Locator("main form button[type=submit], main a[href]").First.ClickAsync();
            await page.WaitForURLAsync(u => !u.Contains("/Account/Profiles", StringComparison.Ordinal));
        }
        return page.Url;
    });
    await Visit("home", () => Go("/"));
    await Visit("discover", () => Go("/Discover"));
    var search = "/?q=" + Uri.EscapeDataString(Environment.GetEnvironmentVariable("WALKTHROUGH_SEARCH") ?? "star");
    await Visit("search", () => Go(search));
    await Visit("library", () => Go("/Library"));
    await Visit("details", () => Find("details", "a[href^='/Details/']", "/Library", search));
    await Visit("player", async () =>
    {
        var path = await Find("player", "a[href^='/Player/']", urls.GetValueOrDefault("details") ?? "/Library");
        if (path is null) return null;
        // The player has started when its video moves or reports an error.
        var state = "no video element";
        for (var i = 0; i < 20 && state is not ("plays" or "error"); i++)
        {
            await page.WaitForTimeoutAsync(500);
            state = await page.EvaluateAsync<string>("() => { const v = document.querySelector('video'); return !v ? 'no video element' : v.error ? 'error' : v.currentTime > 0 ? 'plays' : 'readyState ' + v.readyState; }");
        }
        results[^1].Note = "video: " + state;
        return path;
    });
    await Visit("admin-works", () => Go("/Admin/Works"));
    await Visit("admin-work", () => Find("work", "a[href^='/Admin/Works/']:not([href*='Matches'])", "/Admin/Works"));
    await Visit("admin-queue", () => Go("/Admin/Queue"));
    await Visit("admin-downloads", () => Go("/Admin/Downloads"));
    await Visit("admin-search", () => Go("/Admin/Search"));
    // The search across the indexers answers after the page: the picture waits for the releases (or their failure).
    await Visit("admin-search-results", async () =>
    {
        var path = await Go("/Admin/Search?q=" + Uri.EscapeDataString(Environment.GetEnvironmentVariable("WALKTHROUGH_INDEXER_SEARCH") ?? "Frieren S01E05") + "&type=Search");
        try { await page.Locator("[data-search-results][data-loaded], [data-search-results] .search-results__state").First.WaitForAsync(new() { Timeout = 30_000 }); } catch (TimeoutException) { }
        return path;
    });
    await Visit("admin-system", () => Go("/Admin/System"));
}

// The summary: one line per page and size, then the totals.
var text = new StringBuilder();
text.AppendLine("| page | size | load ms | sideways scroll | console errors | failed requests | note |");
text.AppendLine("|---|---|---|---|---|---|---|");
foreach (var r in results)
{
    var scroll = r.ScrollWidth > r.Width ? $"yes ({r.ScrollWidth} px)" : "no";
    text.AppendLine($"| {r.Page} | {r.Width} | {r.LoadMs?.ToString() ?? "-"} | {scroll} | {r.Console.Count} | {r.Requests.Count} | {r.Note} |");
}
var problems = results.Where(r => r.Console.Count > 0 || r.Requests.Count > 0 || r.ScrollWidth > r.Width || r.Note?.StartsWith("failed") == true).ToList();
text.AppendLine();
text.AppendLine($"{results.Count} views, {results.Count(r => r.ScrollWidth > r.Width)} with sideways scroll, {results.Sum(r => r.Console.Count)} console errors, " +
    $"{results.Sum(r => r.Requests.Count)} failed requests, {results.Count(r => r.Note?.StartsWith("failed") == true)} failed to open, slowest load {results.Max(r => r.LoadMs ?? 0)} ms.");
foreach (var r in problems)
    foreach (var line in r.Console.Select(c => "console: " + c).Concat(r.Requests.Select(q => "request: " + q)).Distinct())
        text.AppendLine($"- {r.Page} @{r.Width}: {line}");
text.AppendLine();
text.AppendLine("Screenshots: " + output);
File.WriteAllText(System.IO.Path.Combine(output, "summary.md"), text.ToString());
Console.Write(text);
return problems.Count == 0 ? 0 : 1;

string Rel(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && url.StartsWith(address, StringComparison.Ordinal) ? u.PathAndQuery : url;
static string Short(string s) => (s.Length > 160 ? s[..160] + "..." : s).Replace('\n', ' ').Replace("|", "\\|");

sealed class Result(string page, string size, int width)
{
    public string Page { get; } = page;
    public string Size { get; } = size;
    public int Width { get; } = width;
    public int? LoadMs { get; set; }
    public int ScrollWidth { get; set; }
    public string? Url { get; set; }
    public string? Note { get; set; }
    public List<string> Console { get; } = [];
    public List<string> Requests { get; } = [];
}
