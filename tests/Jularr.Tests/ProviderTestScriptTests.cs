using Jint;

namespace Jularr.Tests;

/// <summary>
/// provider-test.js runs under Jint against the real script: a connection test of a typed credential is answered in place, so the typed value is
/// never lost between Test and Save, and a result that cannot be read leaves the plain form post to show it.
/// </summary>
[TestClass]
public sealed class ProviderTestScriptTests
{
    private const string Harness = """
        const notice = (text) => ({ text, replaced: null, replaceWith(next) { this.replaced = next; } });
        const result = (hit) => ({ querySelector: (selector) => (selector === '[data-provider-feedback]' ? hit : null) });
        const form = (current) => {
            const heading = { before(node) { form.inserted = node; } };
            const form = { inserted: null, querySelector: (selector) => (selector === '[data-provider-feedback]' ? current : selector === '.prov-subheading' ? heading : null) };
            return form;
        };
        const env = (response, hit) => ({ calls: [], fetch(url, options) { this.calls.push({ url, method: options.method, credentials: options.credentials }); return Promise.resolve(response); }, formData: (f) => ({ form: f }), parse: () => result(hit), importNode: (node) => ({ imported: node.text }) });
        const ok = { ok: true, text: () => Promise.resolve('<html></html>') };
        const test = { formAction: '/Admin/Providers?provider=tmdb&handler=Test' };
        """;

    [TestMethod]
    public void TheResultReplacesTheNoticeInPlaceAndPostsTheFormToTheTestHandler()
    {
        var result = Json("""
            const current = notice('old'); const f = form(current); const e = env(ok, notice('Connection works.'));
            const shown = await window.JularrProviderTest.testInPlace(f, test, e);
            return { shown, replaced: current.replaced, inserted: f.inserted, call: e.calls[0] };
            """);

        Assert.AreEqual("{\"shown\":true,\"replaced\":{\"imported\":\"Connection works.\"},\"inserted\":null,\"call\":{\"url\":\"/Admin/Providers?provider=tmdb&handler=Test\",\"method\":\"POST\",\"credentials\":\"same-origin\"}}", result);
    }

    [TestMethod]
    public void WithoutANoticeYetTheResultIsInsertedBeforeTheConnectionHeading()
    {
        var result = Json("""
            const f = form(null); const e = env(ok, notice('Connection works.'));
            const shown = await window.JularrProviderTest.testInPlace(f, test, e);
            return { shown, inserted: f.inserted };
            """);

        Assert.AreEqual("{\"shown\":true,\"inserted\":{\"imported\":\"Connection works.\"}}", result);
    }

    [TestMethod]
    public void AnAnswerWithoutAReadableResultOrWithAFailureStatusLeavesThePageUntouched()
    {
        var result = Json("""
            const missing = form(notice('old')); const failed = form(notice('old'));
            const noResult = await window.JularrProviderTest.testInPlace(missing, test, env(ok, null));
            const badStatus = await window.JularrProviderTest.testInPlace(failed, test, env({ ok: false, text: () => Promise.resolve('') }, notice('x')));
            return { noResult, badStatus, untouched: missing.inserted === null && failed.inserted === null };
            """);

        Assert.AreEqual("{\"noResult\":false,\"badStatus\":false,\"untouched\":true}", result);
    }

    private static string Json(string script)
    {
        var engine = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(10)));
        engine.Execute("var window = globalThis;");
        engine.Execute(File.ReadAllText(Path.Combine(PlayerControlsTests.RepositoryRoot(), "src", "Jularr.Web", "wwwroot", "js", "provider-test.js")));
        return engine.Evaluate($"(async () => JSON.stringify(await (async () => {{ {Harness} {script} }})()))()").UnwrapIfPromise().AsString();
    }
}
