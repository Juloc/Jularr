using Jint;

namespace Jularr.Tests;

/// <summary>
/// The rules of work-metadata.js (docs/mockups/movie-detail, section 2) run under Jint against the real script: the trailer facade builds one
/// sandboxed no-cookie frame from a validated YouTube key and nothing else, the description expands and collapses with an honest button,
/// and artwork that is gone is dropped. The DOM is a minimal fake the test drives.
/// </summary>
[TestClass]
public sealed class WorkMetadataScriptTests
{
    private const string Harness = """
        const classes = () => { const set = new Set(); return { add: (...names) => names.forEach((n) => set.add(n)), toggle: (n, on) => (on ? set.add(n) : set.delete(n)), contains: (n) => set.has(n), list: () => [...set].sort() }; };
        const facade = (dataset) => ({ dataset, children: [], replaceChildren(child) { this.children = [child]; } });
        const doc = () => ({ created: [], createElement(tag) { const el = { tag, attrs: {}, className: '', focused: false, setAttribute(name, value) { this.attrs[name] = value; }, focus() { this.focused = true; } }; this.created.push(el); return el; } });
        const root = () => ({ classList: classes() });
        const button = () => ({ attrs: {}, hidden: true, textContent: 'Read more', dataset: { more: 'Read more', less: 'Show less' }, setAttribute(name, value) { this.attrs[name] = value; } });
        """;

    [TestMethod]
    public void OnlyAKeyWithTheShapeOfAYouTubeIdBuildsAFrameOnTheNoCookieOriginAndNothingElseIsGranted()
    {
        var result = Json("""
            const frame = window.JularrWorkMetadata.trailerFrame('BdJKm16Co6M', 'Trailer of Moon');
            const rejected = ['', 'short', 'BdJKm16Co6M1', 'x" onload="alert(1)', '../../x', 'https://evil.example/x', null, undefined, 42, {}].map((key) => window.JularrWorkMetadata.trailerFrame(key, 'x'));
            return { frame, rejected };
            """);

        Assert.AreEqual(
            "{\"frame\":{\"src\":\"https://www.youtube-nocookie.com/embed/BdJKm16Co6M?autoplay=1&rel=0&playsinline=1\",\"title\":\"Trailer of Moon\",\"sandbox\":\"allow-scripts allow-same-origin allow-presentation\","
            + "\"allow\":\"autoplay; encrypted-media; picture-in-picture; fullscreen\",\"referrerpolicy\":\"strict-origin-when-cross-origin\"},"
            + "\"rejected\":[null,null,null,null,null,null,null,null,null,null]}",
            result);
        foreach (var forbidden in new[] { "allow-top-navigation", "allow-popups", "allow-forms", "allow-modals", "allow-downloads" })
        {
            Assert.IsFalse(result.Contains(forbidden, StringComparison.Ordinal), $"The frame must not be granted '{forbidden}'.");
        }
    }

    [TestMethod]
    public void StartingTheTrailerReplacesTheFacadeWithOneFrameAndMovesFocusIntoIt()
    {
        var result = Json("""
            const page = doc(); const box = facade({ key: 'BdJKm16Co6M', frameTitle: 'Trailer of Moon' });
            const started = window.JularrWorkMetadata.startTrailer(box, page);
            const frame = box.children[0];
            return { started, count: box.children.length, tag: frame.tag, className: frame.className, focused: frame.focused, src: frame.attrs.src, started2: box.dataset.started };
            """);

        Assert.AreEqual(
            "{\"started\":true,\"count\":1,\"tag\":\"iframe\",\"className\":\"vd-trailer-embed\",\"focused\":true,\"src\":\"https://www.youtube-nocookie.com/embed/BdJKm16Co6M?autoplay=1&rel=0&playsinline=1\",\"started2\":\"true\"}",
            result);
    }

    [TestMethod]
    public void AFacadeWithoutAValidKeyStaysAsItIsSoTheLinkStillWorks()
    {
        var result = Json("""
            const page = doc(); const box = facade({ key: 'bad key', frameTitle: 't' });
            const started = window.JularrWorkMetadata.startTrailer(box, page);
            return { started, created: page.created.length, children: box.children.length, marker: box.dataset.started ?? null };
            """);

        Assert.AreEqual("{\"started\":false,\"created\":0,\"children\":0,\"marker\":null}", result);
    }

    [TestMethod]
    public void TheDescriptionExpandsAndCollapsesAndTheButtonIsOnlyThereWhileTextIsHidden()
    {
        var result = Json("""
            const view = root(); const more = button(); const text = { scrollHeight: 120, clientHeight: 88 };
            const api = window.JularrWorkMetadata;
            api.syncOverview(view, text, more); const cutOff = more.hidden;
            const expanded = api.toggleOverview(view, more); api.syncOverview(view, text, more);
            const open = { expanded, aria: more.attrs['aria-expanded'], label: more.textContent, hidden: more.hidden, classes: view.classList.list() };
            text.scrollHeight = 88; api.syncOverview(view, text, more); const stillThere = more.hidden;
            const collapsed = api.toggleOverview(view, more); api.syncOverview(view, text, more);
            const closed = { collapsed, aria: more.attrs['aria-expanded'], label: more.textContent, hidden: more.hidden, classes: view.classList.list() };
            const fits = root(); const fitsButton = button(); api.syncOverview(fits, { scrollHeight: 66, clientHeight: 66 }, fitsButton);
            return { cutOff, open, stillThere, closed, fitsHidden: fitsButton.hidden };
            """);

        Assert.AreEqual(
            "{\"cutOff\":false,\"open\":{\"expanded\":true,\"aria\":\"true\",\"label\":\"Show less\",\"hidden\":false,\"classes\":[\"is-expanded\"]},\"stillThere\":false,"
            + "\"closed\":{\"collapsed\":false,\"aria\":\"false\",\"label\":\"Read more\",\"hidden\":true,\"classes\":[]},\"fitsHidden\":true}",
            result);
    }

    [TestMethod]
    public void OnlyAPlainPrimaryClickIsTakenOverSoNewTabsAndMiddleClicksFollowTheLink()
    {
        var result = Json("""
            const plain = window.JularrWorkMetadata.isPlainPrimaryClick;
            return {
                primary: plain({ button: 0, metaKey: false, ctrlKey: false, shiftKey: false, altKey: false }),
                middle: plain({ button: 1, metaKey: false, ctrlKey: false, shiftKey: false, altKey: false }),
                right: plain({ button: 2, metaKey: false, ctrlKey: false, shiftKey: false, altKey: false }),
                meta: plain({ button: 0, metaKey: true, ctrlKey: false, shiftKey: false, altKey: false }),
                ctrl: plain({ button: 0, metaKey: false, ctrlKey: true, shiftKey: false, altKey: false }),
                shift: plain({ button: 0, metaKey: false, ctrlKey: false, shiftKey: true, altKey: false }),
                alt: plain({ button: 0, metaKey: false, ctrlKey: false, shiftKey: false, altKey: true })
            };
            """);

        Assert.AreEqual("{\"primary\":true,\"middle\":false,\"right\":false,\"meta\":false,\"ctrl\":false,\"shift\":false,\"alt\":false}", result);
    }

    [TestMethod]
    public void ArtworkThatIsGoneIsRemovedAndAHeroFallsBackToItsGradient()
    {
        var result = Json("""
            const hero = { classList: classes() }; let removed = 0;
            window.JularrWorkMetadata.dropFailedArt({ closest: (selector) => (selector === '[data-work-hero]' ? hero : null), remove() { removed++; } });
            let removedStill = 0;
            window.JularrWorkMetadata.dropFailedArt({ closest: () => null, remove() { removedStill++; } });
            return { removed, classes: hero.classList.list(), removedStill };
            """);

        Assert.AreEqual("{\"removed\":1,\"classes\":[\"ad-hero-derived\",\"vd-hero-plain\"],\"removedStill\":1}", result);
    }

    private static string Json(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "work-metadata.js")));
        return engine.Evaluate($"JSON.stringify((() => {{ {Harness} {script} }})())").AsString();
    }
}
