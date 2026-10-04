using Jularr.Web.Features.Ai;
using Jularr.Web.Features.Books;

namespace Jularr.Tests;

[TestClass]
public sealed class BookTranslationChunkStoreTests
{
    /// <summary>A finished segment must survive a failed run and be found again by the retry, whatever story context surrounds it by then.</summary>
    [TestMethod]
    public async Task SavedSegmentIsResumedForTheSameSourceText()
    {
        var root = Path.Combine(Path.GetTempPath(), $"jularr-chunks-{Guid.NewGuid():N}");
        try
        {
            var store = new BookTranslationChunkStore(root);
            var workId = Guid.NewGuid();
            var chapterId = Guid.NewGuid();
            await store.SaveAsync(workId, chapterId, "de", "hash", 1, AiTranslationMode.Quality, 0, "Source segment", "Fertiges Segment", CancellationToken.None);

            var resumed = await store.TryLoadAsync(workId, chapterId, "de", "hash", 1, AiTranslationMode.Quality, 0, "Source segment", CancellationToken.None);
            var otherText = await store.TryLoadAsync(workId, chapterId, "de", "hash", 1, AiTranslationMode.Quality, 0, "Edited source segment", CancellationToken.None);

            Assert.AreEqual("Fertiges Segment", resumed);
            Assert.IsNull(otherText);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
