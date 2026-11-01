using AngleSharp.Html.Parser;
using Jularr.Backend.Logic.Reading;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ReadingBlocksTests
    {
        private static Jularr.Backend.Data.Logic.Reading.PreparedSection Prepare(string body) =>
            ReadingHtml.Prepare("<html><body>" + body + "</body></html>", "OEBPS/ch.xhtml", 1, new Dictionary<string, int>(), new Dictionary<string, string>());

        [TestMethod]
        public void Prepare_TextInLeafDivs_BecomesBlocksInReadingOrder()
        {
            var prepared = Prepare("<div><span><br></span><p> </p></div><div><span><span>Chapter Twenty-two</span></span></div>"
                + "<div><span><span>The rom phuro came forward.</span></span></div><p>A plain paragraph.</p>");

            var blocks = new HtmlParser().ParseDocument(prepared.Html).QuerySelectorAll("[data-b]").ToArray();
            CollectionAssert.AreEqual(new[] { "", "Chapter Twenty-two", "The rom phuro came forward.", "A plain paragraph." }, blocks.Select(block => block.TextContent.Trim()).ToArray());
            CollectionAssert.AreEqual(new[] { "0", "1", "2", "3" }, blocks.Select(block => block.GetAttribute("data-b")).ToArray());
            Assert.AreEqual(4, prepared.Blocks);
        }

        [TestMethod]
        public void Prepare_ADivHoldingBlocksOrOtherDivs_IsNotABlockItself()
        {
            var prepared = Prepare("<div class=\"chapter\"><div>First line.</div><div>Second line.</div></div><div><p>Inside a paragraph.</p></div>");

            var blocks = new HtmlParser().ParseDocument(prepared.Html).QuerySelectorAll("[data-b]").ToArray();
            CollectionAssert.AreEqual(new[] { "div", "div", "p" }, blocks.Select(block => block.LocalName).ToArray());
            CollectionAssert.AreEqual(new[] { "First line.", "Second line.", "Inside a paragraph." }, blocks.Select(block => block.TextContent.Trim()).ToArray());
        }
    }
}
