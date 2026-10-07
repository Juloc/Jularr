using Jularr.Web.Features.Acquisition;
using Jularr.Web.Features.Acquisition.DownloadClients;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Pages.Settings.DownloadClients;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jularr.Tests;

/// <summary>The SABnzbd client form: saving it must keep every media type's category, including those the form does not show.</summary>
[TestClass]
public sealed class DownloadClientEditTests
{
    [TestMethod]
    public async Task SavingAnExistingClientKeepsTheMusicAndAudiobookCategoriesAndTheFormCanSetThem()
    {
        var directory = SabnzbdTestSupport.CreateTemporaryDirectory();
        try
        {
            await using var db = await MediaCoreTestSupport.CreateDbAsync();
            var store = new DownloadClientStore(new EphemeralDataProtectionProvider(), directory);
            var entry = new DownloadClientEntry(Guid.NewGuid(), "SABnzbd", DownloadClientType.Sabnzbd, true, 1, DownloadClientSettings.CreateDefault("http://sab:8080"), "secret");
            await store.SaveAsync(entry);

            var page = Page(db, store, entry.Id);
            page.Name = "SABnzbd";
            page.BaseUrl = "http://sab:8080";
            page.Priority = 1;
            page.Enabled = true;
            page.MovieCategory = "movies";
            page.MusicCategory = "music-lossless";
            page.AudiobookCategory = "spoken";
            Assert.IsInstanceOfType<RedirectToPageResult>(await page.OnPostAsync(CancellationToken.None));

            var saved = (await store.GetAsync(entry.Id))!;
            Assert.AreEqual("music-lossless", saved.CategoryFor(MediaAcquisitionKind.Music));
            Assert.AreEqual("spoken", saved.CategoryFor(MediaAcquisitionKind.Audiobook));
            Assert.AreEqual("movies", saved.CategoryFor(MediaAcquisitionKind.Movie), "The form keeps the categories it shows.");
            Assert.AreEqual("secret", saved.Secret, "The credential is kept when it is not entered again.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static EditModel Page(Jularr.Web.Data.AppDbContext db, DownloadClientStore store, Guid id)
    {
        var context = new DefaultHttpContext();
        return new EditModel(db, store, NullLogger<EditModel>.Instance)
        {
            Id = id,
            PageContext = new PageContext(new ActionContext(context, new RouteData(), new PageActionDescriptor())),
            TempData = new TempDataDictionary(context, new MemoryTempData())
        };
    }

    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
