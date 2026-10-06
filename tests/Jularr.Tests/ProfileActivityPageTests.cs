using System.Security.Claims;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Progress;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using ActivityIndexModel = Jularr.Web.Pages.Activity.IndexModel;
using ProfileIndexModel = Jularr.Web.Pages.Profile.IndexModel;

namespace Jularr.Tests;

/// <summary>Profile and Activity (#517): lists come from the catalog, history is the user's own.</summary>
[TestClass]
public sealed class ProfileActivityPageTests
{
    [TestMethod]
    public async Task ActivityListsOnlyTheSignedInUsersOwnRequestsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new AcquisitionAccessStore(fixture.Db);
        await store.CreateAsync(Draft("mine"), "user-a", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await store.CreateAsync(Draft("theirs"), "user-b", AcquisitionRequestStatus.Pending, null, CancellationToken.None);
        await store.CreateAsync(Draft("owner"), "owner", AcquisitionRequestStatus.Completed, "owner", CancellationToken.None);

        var user = await fixture.GetActivityAsync("user-a", isOwner: false);
        CollectionAssert.AreEqual(new[] { "mine" }, user.Requests.Select(request => request.ExternalId).ToArray());
        Assert.IsTrue(user.Playback.Count == 0 && user.Reading.Count == 0);

        var owner = await fixture.GetActivityAsync("owner", isOwner: true);
        CollectionAssert.AreEqual(new[] { "owner" }, owner.Requests.Select(request => request.ExternalId).ToArray(), "The owner sees their own history too, not everyone's.");
    }

    [TestMethod]
    public async Task ActivityIsEmptyForANewUserAsync()
    {
        await using var fixture = await Fixture.CreateAsync();

        var page = await fixture.GetActivityAsync("user-new", isOwner: false);

        Assert.IsTrue(page.IsEmpty);
    }

    [TestMethod]
    public async Task ProfileAdminDrillInIsOwnerOnlyAsync()
    {
        await using var fixture = await Fixture.CreateAsync();

        var user = fixture.Attach(new ProfileIndexModel(fixture.Db, Account("user-a", isOwner: false), fixture.Shell), "user-a", isOwner: false);
        Assert.IsInstanceOfType<NotFoundResult>(await user.OnGetAsync("admin", CancellationToken.None));
        Assert.IsInstanceOfType<PageResult>(await user.OnGetAsync("settings", CancellationToken.None));
        Assert.AreEqual("settings", user.Section?.Id);

        var list = fixture.Attach(new ProfileIndexModel(fixture.Db, Account("user-a", isOwner: false), fixture.Shell), "user-a", isOwner: false);
        Assert.IsInstanceOfType<PageResult>(await list.OnGetAsync(null, CancellationToken.None));
        Assert.IsFalse(list.Links.Any(item => item.Id == "admin"));
        CollectionAssert.Contains(list.Elsewhere.Select(item => item.Id).ToArray(), "watchlist", "Destinations outside the bottom bar are listed on Profile.");

        var owner = fixture.Attach(new ProfileIndexModel(fixture.Db, Account("owner", isOwner: true), fixture.Shell), "owner", isOwner: true);
        Assert.IsInstanceOfType<PageResult>(await owner.OnGetAsync("admin", CancellationToken.None));
        Assert.AreEqual("admin", owner.Section?.Id);
    }

    [TestMethod]
    public void ProfileAndActivityPagesAreForAnySignedInUser()
    {
        foreach (var type in new[] { typeof(ProfileIndexModel), typeof(Jularr.Web.Pages.Profile.AccountModel), typeof(ActivityIndexModel) })
        {
            var roles = type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(attribute => attribute.Roles)
                .Where(value => !string.IsNullOrEmpty(value));
            Assert.IsFalse(roles.Any(), $"{type.Name} must not be role-restricted.");
            Assert.IsFalse(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true).Length > 0, type.Name);
        }
    }

    private static AcquisitionRequestDraft Draft(string id) =>
        new(MediaAcquisitionKind.Book, "openlibrary", id, $"Title {id}", null, null);

    private static CurrentAccountContext Account(string profileId, bool isOwner) =>
        new(new FixedHttpContextAccessor(new DefaultHttpContext { User = Principal(profileId, isOwner) }));

    private static ClaimsPrincipal Principal(string profileId, bool isOwner)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, profileId), new(ClaimTypes.Name, profileId) };
        if (isOwner)
        {
            claims.Add(new Claim(ClaimTypes.Role, AccountRoles.Owner));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;

        private Fixture(string directory, AppDbContext db)
        {
            this.directory = directory;
            Db = db;
            Shell = new AppShellService(new MediaCapabilityService(new MediaCapabilityStore(directory)));
        }

        public AppDbContext Db { get; }

        public IAppShellService Shell { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"jularr-profile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "app.db")};Foreign Keys=True")
                .Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);
            return new Fixture(directory, db);
        }

        public async Task<ActivityIndexModel> GetActivityAsync(string profileId, bool isOwner)
        {
            var account = Account(profileId, isOwner);
            var page = Attach(
                new ActivityIndexModel(Db, account, EpisodeFlowFixture.ProgressService(Db, account), new AcquisitionAccessStore(Db)),
                profileId,
                isOwner);
            await page.OnGetAsync(CancellationToken.None);
            return page;
        }

        public TPage Attach<TPage>(TPage page, string profileId, bool isOwner)
            where TPage : PageModel
        {
            page.PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(profileId, isOwner) },
                ViewData = new ViewDataDictionary<TPage>(new EmptyModelMetadataProvider(), new ModelStateDictionary())
            };
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
