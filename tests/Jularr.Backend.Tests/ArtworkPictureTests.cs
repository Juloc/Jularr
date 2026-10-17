using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ArtworkPictureTests
    {
        [TestMethod]
        public void JxlRendition_AdvertisesTypedSourceAndPreservesWebpFallbackDimensions()
        {
            using var output = new StringWriter();
            new MediaImage("/api/v1/works/42/artwork/1", "Poster", 342, 513,
                jxlUrl: "/api/v1/works/42/artwork/1.jxl")
                .WriteTo(output, HtmlEncoder.Default, null!);

            var html = output.ToString();
            StringAssert.Contains(html, "<picture>");
            StringAssert.Contains(html, "<source type=\"image/jxl\" srcset=\"/api/v1/works/42/artwork/1.jxl\">");
            StringAssert.Contains(html, "<img class=\"media-image\" src=\"/api/v1/works/42/artwork/1\"");
            StringAssert.Contains(html, "width=\"342\" height=\"513\"");
            StringAssert.Contains(html, "decoding=\"async\" loading=\"lazy\"");
            Assert.IsTrue(html.IndexOf("<source", StringComparison.Ordinal) < html.IndexOf("<img", StringComparison.Ordinal));
        }

        [TestMethod]
        public void MissingJxlRendition_RendersOrdinaryImageWithNoBrokenSource()
        {
            using var output = new StringWriter();
            new MediaImage("/api/v1/works/42/artwork/1", "Poster", 342, 513).WriteTo(output, HtmlEncoder.Default, null!);
            var html = output.ToString();
            StringAssert.Contains(html, "<img");
            Assert.IsFalse(html.Contains("<picture>", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("image/jxl", StringComparison.Ordinal));
        }

        [TestMethod]
        public void UnsafeJxlUrl_IsRejected()
        {
            using var output = new StringWriter();
            Assert.ThrowsExactly<ArgumentException>(() => new MediaImage("/poster.webp", "Poster", 342, 513,
                jxlUrl: "javascript:alert(1)").WriteTo(output, HtmlEncoder.Default, null!));
        }

        [TestMethod]
        public void PriorityAndStableDimensions_SurvivePictureWrapping()
        {
            using var output = new StringWriter();
            new MediaImage("/poster.webp", "Hero", 1200, 675, priority: true, jxlUrl: "/poster.jxl")
                .WriteTo(output, HtmlEncoder.Default, null!);
            StringAssert.Contains(output.ToString(), "loading=\"eager\" fetchpriority=\"high\"");
            StringAssert.Contains(output.ToString(), "width=\"1200\" height=\"675\"");
        }
    }
}
