using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Service.Admin.Wanted.V1;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NyaaCategoriesTests
    {
        private static QualityProfile Profile(LanguageFallbackType fallback, params string[] languages) =>
            new("1", "Test", ["EPUB"], ["EPUB"], false, "EPUB", null, null) { LanguageOrder = languages, LanguageFallback = fallback };

        [TestMethod]
        public void Categories_AnyFallback_AsksEnglishTranslatedAndRawBesidesTheWantedLanguage()
        {
            var categories = NyaaSources.Categories(Profile(LanguageFallbackType.Any, "de"));

            CollectionAssert.AreEquivalent(new[] { "3_1", "3_2", "3_3" }, categories.Keys.ToArray());
        }

        [TestMethod]
        public void Categories_NoneFallback_AsksOnlyTheWantedLanguage()
        {
            var categories = NyaaSources.Categories(Profile(LanguageFallbackType.None, "de"));

            CollectionAssert.AreEquivalent(new[] { "3_2" }, categories.Keys.ToArray());
        }
    }
}
