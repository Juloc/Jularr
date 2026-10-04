using System.Security.Claims;
using Jularr.Tests.Infrastructure;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Events;
using Jularr.Web.Features.Notifications;
using Jularr.Web.Pages.Profile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace Jularr.Tests;

[TestClass]
public sealed class ProfileNotificationSettingsTests
{
    [TestMethod]
    public async Task GetReadsCanonicalPreferencesAndHidesAdminTopicsFromNormalProfiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.Page();

        await page.OnGetAsync(CancellationToken.None);

        var release = page.Preferences[JularrEventCategory.ReleaseAvailable];
        Assert.IsTrue(release.Enabled);
        Assert.IsFalse(release.IsExplicit);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, release.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, release.Channels.ToArray());
        Assert.IsFalse(page.VisibleCategories.Contains(JularrEventCategory.StorageProblem));
    }

    [TestMethod]
    public async Task PostWritesOffAndInAppThroughCanonicalPreferenceAggregate()
    {
        await using var fixture = await Fixture.CreateAsync();

        var offPage = fixture.Page((JularrEventCategory.ReleaseAvailable, NotificationsModel.OffFormValue));
        await offPage.OnPostAsync(CancellationToken.None);

        var disabled = await fixture.Store.GetEventPreferenceAsync(Fixture.ProfileId, JularrEventCategory.ReleaseAvailable);
        Assert.IsFalse(disabled.Enabled);
        Assert.IsTrue(disabled.IsExplicit);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, disabled.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, disabled.Channels.ToArray());

        var inAppPage = fixture.Page((JularrEventCategory.ReleaseAvailable, NotificationsModel.InAppFormValue));
        await inAppPage.OnPostAsync(CancellationToken.None);

        var enabled = await fixture.Store.GetEventPreferenceAsync(Fixture.ProfileId, JularrEventCategory.ReleaseAvailable);
        Assert.IsTrue(enabled.Enabled);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, enabled.Timing);
        CollectionAssert.AreEquivalent(new[] { NotificationChannel.InApp }, enabled.Channels.ToArray());
    }

    [TestMethod]
    public async Task NormalProfileCannotMutateAdminCategoryByPostingItsFormField()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = fixture.Page((JularrEventCategory.StorageProblem, NotificationsModel.OffFormValue));

        await page.OnPostAsync(CancellationToken.None);

        var storage = await fixture.Store.GetEventPreferenceAsync(Fixture.ProfileId, JularrEventCategory.StorageProblem);
        Assert.IsTrue(storage.Enabled);
        Assert.IsFalse(storage.IsExplicit);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string ProfileId = "notification-settings-user";

        private readonly DefaultHttpContext context;
        private readonly AppDbContext db;
        private readonly CurrentAccountContext account;

        private Fixture(DefaultHttpContext context, AppDbContext db)
        {
            this.context = context;
            this.db = db;
            var accessor = new HttpContextAccessor { HttpContext = context };
            account = new CurrentAccountContext(accessor);
            Store = new NotificationSubscriptionStore(db);
        }

        public NotificationSubscriptionStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = TestPostgres.ResolveConnectionString($"Data Source=profile-notification-settings-{Guid.NewGuid():N}.db");
            var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
            await DatabaseMigrationBridge.UpgradeAsync(db);

            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, ProfileId), new Claim(ClaimTypes.Role, AccountRoles.User)],
                    "test"))
            };

            return new Fixture(context, db);
        }

        public NotificationsModel Page(params (JularrEventCategory Category, string Value)[] values)
        {
            var form = values.ToDictionary(
                value => $"delivery.{value.Category}",
                value => new StringValues(value.Value),
                StringComparer.Ordinal);
            context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(form)));

            var page = new NotificationsModel(db, Store, account)
            {
                PageContext = new PageContext { HttpContext = context },
                TempData = new TempDataDictionary(context, new NoTempDataProvider())
            };
            return page;
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private sealed class NoTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
