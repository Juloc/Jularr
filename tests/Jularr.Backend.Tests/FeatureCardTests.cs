using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class FeatureCardTests
    {
        [TestMethod]
        [DataRow(4_700L, 14_800L, 68)]
        [DataRow(0L, 1_000L, 100)]
        [DataRow(1_000L, 1_000L, 0)]
        [DataRow(2_000L, 1_000L, 0)]
        public void SpaceBar_Percent_IsTheUsedShareOfAMeasuredVolume(long free, long total, int expected) => Assert.AreEqual(expected, SpaceBar.Percent(free, total));

        [TestMethod]
        [DataRow(null, 100L)]
        [DataRow(10L, null)]
        [DataRow(10L, 0L)]
        public void SpaceBar_Percent_IsEmptyWithoutAMeasurement(long? free, long? total) => Assert.IsNull(SpaceBar.Percent(free, total));

        [TestMethod]
        public void SpaceBar_Unmeasured_SaysWhyInsteadOfShowingAStaleBar()
        {
            var html = Render(new SpaceBar(null, null, "Free space", "", "", "Not reachable"));
            StringAssert.Contains(html, "value=\"0\"");
            StringAssert.Contains(html, "Not reachable");
            Assert.IsFalse(html.Contains("<strong>", StringComparison.Ordinal));
        }

        [TestMethod]
        public void FeatureCard_EncodesItsTexts_AndWithAnIdIsARegionOfItsOwn()
        {
            var card = new FeatureCard(new IconTile(new Span()), "<b>NAS</b>", "SMB · 1 TB", null, [new Div()], [new Span()], null, "storage-7");
            var html = Render(card);
            Assert.IsFalse(html.Contains("<b>NAS</b>", StringComparison.Ordinal), html);
            StringAssert.Contains(html, "&lt;b&gt;NAS&lt;/b&gt;");
            StringAssert.Contains(html, "id=\"storage-7\"");
            StringAssert.Contains(html, "data-region");
            Assert.IsFalse(Render(new FeatureCard(new IconTile(new Span()), "NAS", null, null, [], [])).Contains("data-region", StringComparison.Ordinal));
        }

        private static string Render(IUiComponent component)
        {
            using var writer = new StringWriter();
            component.WriteTo(writer, HtmlEncoder.Default, null!);
            return writer.ToString();
        }
    }
}
