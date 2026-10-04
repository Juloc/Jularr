namespace Jularr.Tests;

[TestClass]
public sealed class NovelReaderDesignTests
{
    [TestMethod]
    public void ReaderUsesChapterDrawerAndStableSvgChrome()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Novels",
            "Read.cshtml"));

        var drawer = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Novels",
            "_NovelChapterDrawer.cshtml"));

        // The chapter list is the "Contents" tab of the frame's contents panel.
        StringAssert.Contains(page, "data-reader-contents-toggle aria-controls=\"novel-reader-contents\"");
        StringAssert.Contains(page, "data-reader-contents-tab=\"chapters\"");
        StringAssert.Contains(page, "_NovelChapterDrawer");
        StringAssert.Contains(page, "data-chapters-url");
        StringAssert.Contains(drawer, "data-chapter-drawer");
        StringAssert.Contains(drawer, "data-chapter-list");
        StringAssert.Contains(page, "<svg");
        Assert.IsFalse(page.Contains("🔖", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains("✎", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WorkPageUsesSharedAniListProgressCardLoadedAfterFirstPaint()
    {
        var root = FindRepositoryRoot();
        var novels = Path.Combine(root, "src", "Jularr.Web", "Pages", "Novels");
        var page = File.ReadAllText(Path.Combine(novels, "Work.cshtml"));
        var model = File.ReadAllText(Path.Combine(novels, "Work.cshtml.cs"));

        StringAssert.Contains(page, "<partial name=\"_ExternalProgress\"");
        StringAssert.Contains(model, "GetNovelProgressSummaryAsync");
        StringAssert.Contains(model, "OnGetExternalProgressAsync");
        StringAssert.Contains(model, "ExternalProgressMediaKind.Novel");
        Assert.IsFalse(model.Contains("GetNovelProgressPreviewAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReaderDoesNotEmbedTheFullChapterIndex()
    {
        var root = FindRepositoryRoot();
        var novels = Path.Combine(root, "src", "Jularr.Web", "Pages", "Novels");
        var page = File.ReadAllText(Path.Combine(novels, "Read.cshtml"));
        var drawer = File.ReadAllText(Path.Combine(novels, "_NovelChapterDrawer.cshtml"));

        Assert.IsFalse(page.Contains("data-chapter-data", StringComparison.Ordinal));
        Assert.IsFalse(drawer.Contains("data-chapter-data", StringComparison.Ordinal));
        Assert.IsFalse(page.Contains("Model.Chapters", StringComparison.Ordinal));
        Assert.IsFalse(drawer.Contains("Model.Chapters", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReaderScriptsAreFocusedModulesUnderOneBootstrap()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "Pages", "Novels", "Read.cshtml"));
        var js = Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js");
        var bootstrap = File.ReadAllText(Path.Combine(js, "novel-reader.js"));
        var modules = new[]
        {
            "novel-position.js",
            "novel-annotations.js",
            "novel-chapter-drawer.js",
            "novel-translation.js"
        };

        var bootstrapIndex = page.IndexOf("~/js/novel-reader.js", StringComparison.Ordinal);
        foreach (var module in modules)
        {
            var source = File.ReadAllText(Path.Combine(js, module));
            StringAssert.Contains(source, "window.JularrNovelReader");
            Assert.IsFalse(
                source.Contains("document.querySelector(\"[data-novel-reader]\")", StringComparison.Ordinal),
                $"{module} must not bootstrap itself.");
            var moduleIndex = page.IndexOf("~/js/" + module, StringComparison.Ordinal);
            Assert.IsTrue(moduleIndex >= 0 && moduleIndex < bootstrapIndex, $"{module} loads before the bootstrap.");
        }

        StringAssert.Contains(bootstrap, "document.querySelector(\"[data-novel-reader]\")");
        StringAssert.Contains(bootstrap, "modules.position(reader)");
        StringAssert.Contains(bootstrap, "modules.annotations(reader)");
        StringAssert.Contains(bootstrap, "modules.chapterDrawer(reader)");
        StringAssert.Contains(bootstrap, "modules.translation(reader)");
    }

    [TestMethod]
    public void SavedHighlightsRenderAsFlatSegmentsSoOverlapsSurvive()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "wwwroot", "js", "novel-annotations.js"));
        var css = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "wwwroot", "css", "novel-reader-panels.css"));

        StringAssert.Contains(script, "buildHighlightSegments");
        StringAssert.Contains(script, "highlightIds");
        Assert.IsFalse(script.Contains("surroundContents", StringComparison.Ordinal));
        StringAssert.Contains(css, "data-highlight-depth=\"2\"");
    }

    [TestMethod]
    public void BilingualReaderPairsLanguagesBySegment()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Novels",
            "Read.cshtml"));
        var css = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "wwwroot",
            "css",
            "novels.css"));

        StringAssert.Contains(page, "data-reader-segment");
        StringAssert.Contains(page, "data-language=\"ja\"");
        StringAssert.Contains(page, "data-language=\"de\"");
        StringAssert.Contains(
            css,
            ".novel-reader-shell[data-view=\"both\"] .novel-reader-segment");
        StringAssert.Contains(
            css,
            ".novel-reader-shell[data-view=\"both\"] .novel-reader-paragraph.de");
    }

    [TestMethod]
    public void ReaderUsesTheFrameProgressBarAndKeepsTheAppSidebar()
    {
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "novels.css"));
        var shellCss = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "reader-shell.css"));
        var view = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Novels", "Read.cshtml"));

        // Progress lives in the frame's bottom bar, not in a rail over the text.
        StringAssert.Contains(view, "data-reader-progress-slider");
        Assert.IsFalse(css.Contains(".novel-reader-progress-rail", StringComparison.Ordinal));

        // The Jularr sidebar (with its Current card) stays next to the reader;
        // only the phone bottom navigation gives way to the reader's tool row.
        Assert.IsFalse(css.Contains("body:has(.novel-reader-shell) .sidebar", StringComparison.Ordinal));
        StringAssert.Contains(shellCss, "body:has([data-reader-frame]) .mobile-nav");
    }

    [TestMethod]
    public void MobileLanguageSwitchOpensAndClosesInsteadOfCoveringTheText()
    {
        // #487 item 6: the #460 rule pinned the switch over the text permanently
        // and left a "Sprache" button toggling a class without effect.
        var root = FindRepositoryRoot();
        var css = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "reader-shell.css"));
        var js = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "reader-shell.js"));
        var view = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Novels", "Read.cshtml"));

        Assert.IsFalse(
            css.Contains(".novel-reader-toolbar .novel-view-switch {", StringComparison.Ordinal),
            "No permanently pinned language switch.");
        const string expanded = "[data-unified-reader].reader-language-expanded [data-reader-language-control]";
        Assert.AreEqual(1, css.Split(expanded).Length - 1, "One expanded-panel rule, no duplicated selectors.");
        StringAssert.Contains(
            css,
            "[data-unified-reader]:has([data-reader-mobile-actions]):not(.reader-language-expanded) [data-reader-language-control]");

        // The generated button exists only with its control and reports its state.
        StringAssert.Contains(js, "if (!source) return null;");
        StringAssert.Contains(js, "button.setAttribute(\"aria-expanded\", expanded ? \"true\" : \"false\");");

        // The Novel frame's phone More menu opens the language menu itself.
        StringAssert.Contains(view, "class=\"reader-frame-mobile\" data-reader-menu-toggle=\"language\"");
    }

    [TestMethod]
    public void ReaderDefersChapterDomAndActivatesTranslationWithoutReload()
    {
        var root = FindRepositoryRoot();
        var js = Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js");
        var drawer = File.ReadAllText(Path.Combine(js, "novel-chapter-drawer.js"));
        var translation = File.ReadAllText(Path.Combine(js, "novel-translation.js"));
        var page = File.ReadAllText(Path.Combine(
            root, "src", "Jularr.Web", "Pages", "Novels", "Read.cshtml"));

        StringAssert.Contains(drawer, "ensureChapterRows");
        StringAssert.Contains(translation, "installGermanParagraphs");
        StringAssert.Contains(translation, "waitForTranslation");
        StringAssert.Contains(page, "\"TranslationStatus\"");
        foreach (var file in Directory.GetFiles(js, "novel-*.js"))
        {
            Assert.IsFalse(
                File.ReadAllText(file).Contains("window.location.reload", StringComparison.OrdinalIgnoreCase),
                Path.GetFileName(file));
        }
    }

    [TestMethod]
    public void NovelPagesAvoidDecorativeAiStyleSubheadings()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Novels",
            "Index.cshtml"));
        var work = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Novels",
            "Work.cshtml"));
        // The chapter row markup (current/earlier indicator) lives in a shared partial
        // (#512) so a grouped and an ungrouped chapter list draw the exact same row.
        var chapterRow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Jularr.Web",
            "Pages",
            "Novels",
            "_NovelChapterRow.cshtml"));

        Assert.IsFalse(index.Contains("eyebrow", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(index.Contains(
            "reading progress is saved automatically",
            StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(work.Contains(
            "Source cache, AniList metadata and anime matching",
            StringComparison.Ordinal));
        Assert.IsFalse(work.Contains(
            "Manual mappings are authoritative",
            StringComparison.Ordinal));
        StringAssert.Contains(chapterRow, "isEarlier");
    }

    [TestMethod]
    [DataRow("◇◇◇")]
    [DataRow("＊　＊　＊")]
    [DataRow(" * * * ")]
    [DataRow("◆")]
    [DataRow("※※※")]
    public void SceneBreakLinesAreRecognised(string text)
    {
        Assert.IsTrue(Jularr.Web.Features.Novels.NovelChapterDocument.IsSceneBreak(text));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("……")]
    [DataRow("――――")]
    [DataRow("・・・")]
    [DataRow("「……」")]
    [DataRow("◇ 第二章 ◇")]
    [DataRow("◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇◇")]
    public void PausesAndTextAreNotSceneBreaks(string text)
    {
        Assert.IsFalse(Jularr.Web.Features.Novels.NovelChapterDocument.IsSceneBreak(text));
    }

    [TestMethod]
    public void SceneBreakBlocksKeepTheirParagraphIndex()
    {
        var blocks = Jularr.Web.Features.Novels.NovelChapterDocument.BuildReaderBlocks(
            "最初の段落。\n\n◇◇◇\n\n次の場面。",
            null);

        Assert.AreEqual(3, blocks.Count);
        Assert.IsFalse(blocks[0].IsSceneBreak);
        Assert.IsTrue(blocks[1].IsSceneBreak);
        Assert.AreEqual(1, blocks[1].ParagraphIndex);
        Assert.AreEqual(2, blocks[2].ParagraphIndex);
    }

    [TestMethod]
    public void ReaderDrawsSceneBreaksAsSeparatorsThatReadAloudSkips()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Novels", "Read.cshtml"));
        var css = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "novel-reader-frame.css"));
        var tts = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "reader-tts.js"));

        StringAssert.Contains(view, "block.IsSceneBreak ? \" is-scene-break\" : \"\"");
        StringAssert.Contains(view, "role=\"@(block.IsSceneBreak ? \"separator\" : null)\"");
        // The mark stays in the DOM (paragraph offsets) but is only visually hidden.
        StringAssert.Contains(css, ".novel-reader-segment.is-scene-break .novel-reader-paragraph {");
        StringAssert.Contains(css, "clip-path: inset(50%);");
        StringAssert.Contains(tts, "!element.closest('[role=\"separator\"]')");
    }

    [TestMethod]
    public void ContentsRowsReadLikeATableOfContents()
    {
        var root = FindRepositoryRoot();
        var drawer = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "novel-chapter-drawer.js"));
        var partial = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Novels", "_NovelChapterDrawer.cshtml"));

        StringAssert.Contains(drawer, "t(\"chapterNumber\", \"Chapter {number}\", { number: chapter.number })");
        StringAssert.Contains(drawer, "if (!isSpecialChapter(titleText) && !numberingPattern.test(titleText))");
        StringAssert.Contains(drawer, "\"prolog(?:ue)?\"");
        StringAssert.Contains(drawer, "\"プロローグ\"");
        StringAssert.Contains(drawer, "link.setAttribute(\"aria-current\", \"page\")");
        // The current chapter is marked in the list; no extra strip above it.
        Assert.IsFalse(partial.Contains("novel-drawer-current", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReaderPanelsUseNoColouredLeftStripes()
    {
        // Border and inset-shadow stripes are caught for every stylesheet by CssAccentStripeTests;
        // this guards the pseudo-element marker the contents list used to draw.
        var root = FindRepositoryRoot();
        var shell = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "css", "reader-shell.css"));
        Assert.IsFalse(shell.Contains(".reader-contents-row[aria-current=\"page\"]::before", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PagedModeSplitsLongParagraphsAndAnchorsByCharacter()
    {
        var root = FindRepositoryRoot();
        var web = Path.Combine(root, "src", "Jularr.Web", "wwwroot");
        var css = File.ReadAllText(Path.Combine(web, "css", "novels.css")).ReplaceLineEndings("\n");
        var js = File.ReadAllText(Path.Combine(web, "js", "reader-personalization.js"));

        StringAssert.Contains(
            css,
            ".novel-reader-shell[data-reading-mode=\"paged\"] .novel-reader-paragraph {\n    break-inside: auto;");
        StringAssert.Contains(css, ".novel-reader-paragraph.is-heading {");
        StringAssert.Contains(css, ".novel-reader-segment.is-scene-break,");

        // Progress and bookmarks keep the offset of a paragraph that continues from the previous page.
        StringAssert.Contains(js, "setFormValue(data, \"anchorOffset\", anchor?.offset ?? 0);");
        StringAssert.Contains(js, "setFormValue(data, \"characterOffset\", anchor?.offset ?? 0);");
        StringAssert.Contains(js, "offset: Math.max(0, Number(shell.dataset.anchorOffset) || 0)");
        Assert.IsFalse(js.Contains("setFormValue(data, \"anchorOffset\", 0);", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SakuraEffectStaysPausedInsideTheReaders()
    {
        var root = FindRepositoryRoot();
        var web = Path.Combine(root, "src", "Jularr.Web");
        var sakura = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "sakura.js"));

        StringAssert.Contains(sakura, "const inReader = document.querySelector('[data-reader-frame]') !== null;");
        StringAssert.Contains(sakura, "const isDisabled = () => mode === 'off' || reducedMotion.matches || inReader;");
        foreach (var reader in new[] { "Books", "Novels", "Manga" })
        {
            var page = File.ReadAllText(Path.Combine(web, "Pages", reader, "Read.cshtml"));
            Assert.IsTrue(
                System.Text.RegularExpressions.Regex.IsMatch(page, @"\sdata-reader-frame[\s>]"),
                $"{reader}/Read must mark its reader frame so Sakura pauses.");
        }
    }

    [TestMethod]
    public void LanguageMenuIsAlwaysRenderedAndHostsTheTranslateSlot()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "Pages", "Novels", "Read.cshtml"));

        Assert.IsFalse(view.Contains("hasLanguageChoice", StringComparison.Ordinal));
        // The slot always exists inside the menu, so novel-translation.js adds the
        // AI / local source switch there instead of above the text.
        var menu = view.IndexOf("data-reader-menu=\"language\"", StringComparison.Ordinal);
        var slot = view.IndexOf("<div class=\"reader-menu-translate\" data-translation-slot>", StringComparison.Ordinal);
        var gate = view.IndexOf("@if (canTranslate)", slot, StringComparison.Ordinal);
        Assert.IsTrue(menu > 0 && slot > menu && gate > slot);
    }

    [TestMethod]
    public void AppearanceCardsSettingSeveralKeysKeepEveryValue()
    {
        // A reading-mode card sets readingMode and chapterStyle at once; the first
        // save's response must not reset the second value.
        var root = FindRepositoryRoot();
        var js = File.ReadAllText(Path.Combine(root, "src", "Jularr.Web", "wwwroot", "js", "reader-personalization.js"));

        StringAssert.Contains(js, "if (state[key] === snapshot[key]) merged[key] = value;");
        Assert.IsFalse(js.Contains("state = { ...state, ...saved };", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
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
