using System.Text.RegularExpressions;
using Jularr.Web.Features.Localization;

namespace Jularr.Tests;

/// <summary>
/// Interactive page turning for the Books paged reader (#446): a CSS-only page-stack
/// depth effect at the book edges, drag/swipe-driven page turning that follows the
/// pointer and settles by distance/velocity, and a quick toggle between animated and
/// instant paging. Everything here must keep driving the paged reader's existing
/// currentView/goToView() pagination (books-reader.js) rather than a parallel one.
/// </summary>
[TestClass]
public sealed class BookReaderPageTurnTests
{
    [TestMethod]
    public void ReadPageRendersPageStacksAndTheAnimationToggle()
    {
        var page = Read("src", "Jularr.Web", "Pages", "Books", "Read.cshtml");

        StringAssert.Contains(page, "data-book-stack=\"left\"");
        StringAssert.Contains(page, "data-book-stack=\"right\"");
        StringAssert.Contains(page, "data-book-turn-shade");

        // The stacks and the turn-shade live inside the paper spread, not the PDF path.
        var spreadStart = page.IndexOf("data-book-spread", StringComparison.Ordinal);
        var flowStart = page.IndexOf("data-book-flow", StringComparison.Ordinal);
        Assert.IsTrue(spreadStart >= 0 && flowStart > spreadStart);
        var spreadHeader = page[spreadStart..flowStart];
        StringAssert.Contains(spreadHeader, "data-book-stack=\"left\"");
        StringAssert.Contains(spreadHeader, "data-book-stack=\"right\"");
        StringAssert.Contains(spreadHeader, "data-book-turn-shade");

        // Spread and page-animation choices have one owner each: the settings panel and the
        // appearance sheet. The More menu repeats neither, so the top bar stays a short list.
        Assert.IsFalse(page.Contains("data-book-page-turn-choice", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains("data-book-spread-choice", StringComparison.Ordinal));
        var panel = Read("src", "Jularr.Web", "Pages", "Shared", "_ReaderSettingsPanel.cshtml");
        StringAssert.Contains(panel, "data-book-setting=\"pageTransition\"");
        StringAssert.Contains(panel, "data-book-setting=\"twoPageSpread\"");
    }

    [TestMethod]
    public void ReusedCatalogKeysForTheToggleExist()
    {
        Assert.IsTrue(UiTranslationResources.TryGet("reader.settings.pageTransition.curl", out _));
        Assert.IsTrue(UiTranslationResources.TryGet("reader.settings.pageTransition.none", out _));
    }

    [TestMethod]
    public void DragTurnFeedsTheExistingPaginationInsteadOfAParallelOne()
    {
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");

        // The drag activates only for the paged book layout, never for PDFs, the
        // "both" side-by-side view (layout.paged is false there) and never when the
        // reader is mid-restore or animations are turned off.
        StringAssert.Contains(script, "const dragEnabled = () =>");
        StringAssert.Contains(script, "!pdfContainer && layout.paged");
        StringAssert.Contains(script, "settings.pageTransition !== \"none\" && !reduceMotion.matches");

        // A committed drag calls the same goToView()/currentView pagination as
        // keyboard, tap-zones and the progress slider; a cancelled drag springs back
        // to the unchanged currentView using the very transform/transition already
        // used elsewhere, not a second page-turn system.
        StringAssert.Contains(script, "goToView(currentView + direction)");
        StringAssert.Contains(script, "columns.style.transform = `translate3d(${-currentView * layout.stride}px,0,0)`");
        StringAssert.Contains(script, "columns.classList.add(\"is-turning\")");

        // Release decides by distance and by a flick's velocity, not a single fixed
        // 50% rule (#446 "avoid a fragile single hardcoded 50% rule").
        StringAssert.Contains(script, "DRAG_COMMIT_RATIO");
        StringAssert.Contains(script, "DRAG_FLICK_PX_MS");
        Assert.IsFalse(script.Contains("> 0.5)", StringComparison.Ordinal));

        // Shared tap/swipe/keyboard navigation dispatches jularr:reader-page-edge
        // from reader-shell.js. The renderer-specific visual drag only swallows
        // the pointerup it actually committed, so one gesture still turns once.
        StringAssert.Contains(script, "event.stopPropagation()");
        StringAssert.Contains(script, "jularr:reader-page-edge");
    }

    [TestMethod]
    public void StackDepthAndDragRespectReducedMotionAndPagedState()
    {
        var script = Read("src", "Jularr.Web", "wwwroot", "js", "books-reader.js");

        StringAssert.Contains(script, "const updateStackDepth = () => {");
        StringAssert.Contains(script, "--book-stack-left");
        StringAssert.Contains(script, "--book-stack-right");
        // Scroll mode shows no stacks; layout.paged is the single source of truth,
        // matching the mode already exposed as data-book-layout in the CSS/markup.
        var stackDepthStart = script.IndexOf("const updateStackDepth = () => {", StringComparison.Ordinal);
        var stackDepthBody = script.Substring(stackDepthStart, 800);
        StringAssert.Contains(stackDepthBody, "if (!layout.paged) {");
        StringAssert.Contains(stackDepthBody, "spread.style.removeProperty(\"--book-stack-left\");");
        StringAssert.Contains(stackDepthBody, "spread.style.removeProperty(\"--book-stack-right\");");

        var css = Read("src", "Jularr.Web", "wwwroot", "css", "book-reader.css");
        StringAssert.Contains(css, ".book-stack {");
        StringAssert.Contains(css, ".book-stack-left {");
        StringAssert.Contains(css, ".book-stack-right {");
        StringAssert.Contains(css, ".book-turn-shade {");
        StringAssert.Contains(css, ".book-columns.is-dragging");

        var reducedMotionBlock = Regex.Match(
            css,
            @"@media \(prefers-reduced-motion: reduce\) \{(?<body>.*?)\n\}",
            RegexOptions.Singleline).Groups["body"].Value;
        Assert.IsFalse(string.IsNullOrEmpty(reducedMotionBlock), "book-reader.css must define a prefers-reduced-motion block.");
        StringAssert.Contains(reducedMotionBlock, ".book-columns.is-dragging");
        StringAssert.Contains(reducedMotionBlock, ".book-stack");
        StringAssert.Contains(reducedMotionBlock, ".book-turn-shade");
    }

    [TestMethod]
    public void PageStackCssHasNoLeftEdgeAccentStripe()
    {
        // Belt-and-suspenders alongside CssAccentStripeTests: the new stack/shade
        // rules must stay tinted-background/shadow only, never a solid coloured bar.
        var css = Read("src", "Jularr.Web", "wwwroot", "css", "book-reader.css");
        Assert.IsFalse(Regex.IsMatch(css, @"\.book-stack[^{]*\{[^}]*border-left\s*:\s*(?!0)"));
        Assert.IsFalse(Regex.IsMatch(css, @"\.book-turn-shade[^{]*\{[^}]*border-left\s*:\s*(?!0)"));
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
