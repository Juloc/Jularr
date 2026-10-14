using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Notification;
using Jularr.Backend.Data.Public.Authentication.V1;
using Jularr.Backend.Data.User.Notification.V1;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class NotificationTests
    {
        [TestMethod]
        public async Task Notifications_RequestDecisions_ReachTheRequestingProfileUnlessMuted()
        {
            await using var host = await Host.CreateAsync(razor: true, media: true);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (10, 1, '<Work>'), (20, 3, 'Book');
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") VALUES
                    (100, 10, 101, 42, 1), (101, 20, 202, 42, 2), (102, 20, 101, 42, 1), (104, 20, 202, 41, 1);
                """);
            using var state = await host.SendAsync(HttpMethod.Get, "/api/v1/auth");
            using var login = await host.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginDataV1("a@example.test", "test-password-123"));
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync());

            // Approval tells the requester; a rejection tells its own requester only; deciding one's own request tells nobody.
            await DecideAsync(host, 100, AcquisitionRequestStatusType.Approved);
            await DecideAsync(host, 101, AcquisitionRequestStatusType.Rejected);
            await DecideAsync(host, 104, AcquisitionRequestStatusType.Rejected);
            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Works" ("Id", "MediaTypeId", "CanonicalTitle") VALUES (11, 2, '<Work>');
                INSERT INTO "AcquisitionRequests" ("Id", "WorkId", "RequestedByProfileId", "RequestedByAccountId", "UiLocaleId") VALUES (103, 11, 101, 42, 1);
                """);
            await DecideAsync(host, 103, AcquisitionRequestStatusType.Approved);
            Assert.AreEqual(2, await UnreadAsync(host));
            var notice = (await ReadAsync<PageResult<NotificationV1>>(host, "/api/v1/notifications")).Items.Single(item => item.Link == "/Details/10");
            Assert.AreEqual(NotificationType.RequestApproved, notice.NotificationTypeId);
            Assert.AreEqual("<Work>", notice.Subject);
            Assert.AreEqual("/Details/10", notice.Link);
            Assert.AreEqual((short)2, await host.ScalarAsync("SELECT \"NotificationTypeId\" FROM \"Notifications\" WHERE \"ProfileId\" = 202"));

            // The shell shows the count; the inbox encodes the subject.
            using var home = await host.SendAsync(HttpMethod.Get, "/");
            StringAssert.Contains(await home.Content.ReadAsStringAsync(), "class=\"app-notifications__count\">2<");
            using var inbox = await host.SendAsync(HttpMethod.Get, "/Notifications");
            StringAssert.Contains(await inbox.Content.ReadAsStringAsync(), "Your request for &lt;Work&gt; was approved.");

            // A muted kind stays silent; read notices leave the count.
            using var muted = await host.SendAsync(HttpMethod.Put, "/api/v1/notifications/settings", new UpdateNotificationSettingDataV1(NotificationType.RequestApproved, true));
            Assert.AreEqual(HttpStatusCode.OK, muted.StatusCode, await muted.Content.ReadAsStringAsync());
            await DecideAsync(host, 102, AcquisitionRequestStatusType.Approved);
            Assert.AreEqual(2, await UnreadAsync(host));
            Assert.IsTrue((await ReadAsync<BaseDataList<NotificationSettingV1>>(host, "/api/v1/notifications/settings")).Single(setting => setting.NotificationTypeId == NotificationType.RequestApproved).IsMuted);
            using var read = await host.SendAsync(HttpMethod.Put, "/api/v1/notifications/read");
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            Assert.AreEqual(0, await UnreadAsync(host));

            // Switched off, nothing is told and nothing is read.
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = FALSE WHERE \"Key\" = 'Notifications'");
            using var off = await host.SendAsync(HttpMethod.Get, "/api/v1/notifications");
            Assert.AreEqual(HttpStatusCode.Forbidden, off.StatusCode);
            using var homeOff = await host.SendAsync(HttpMethod.Get, "/");
            Assert.IsFalse((await homeOff.Content.ReadAsStringAsync()).Contains("app-notifications", StringComparison.Ordinal));
        }

        private static async Task DecideAsync(Host host, long requestId, AcquisitionRequestStatusType decision)
        {
            using var decided = await host.SendAsync(HttpMethod.Put, "/api/v1/admin/requests/" + requestId, new UpdateRequestDataV1(decision, 1));
            Assert.AreEqual(HttpStatusCode.OK, decided.StatusCode, await decided.Content.ReadAsStringAsync());
        }

        private static async Task<int> UnreadAsync(Host host) => (await ReadAsync<UnreadNotificationsV1>(host, "/api/v1/notifications/unread")).UnreadCount;

        private static async Task<T> ReadAsync<T>(Host host, string path)
        {
            using var response = await host.SendAsync(HttpMethod.Get, path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
    }
}
