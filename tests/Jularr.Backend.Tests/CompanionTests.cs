using System.Net;
using System.Net.Http.Json;
using Jularr.Backend.Data.Public.Device.V1;
using Jularr.Backend.Data.User.Device.V1;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Service.Core;
using Host = Jularr.Backend.Tests.AuthenticationTests.Host;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class CompanionTests
    {
        private static readonly StartPlaybackDataV1 Browser = new(["mp4", "webm"], ["h264", "vp9"], ["aac", "opus"]);

        [TestMethod]
        public async Task Invitation_IsOneUseShortLivedAndBoundToTvAndProfile()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (tvToken, playbackId) = await PrepareTvAsync(host, storage.Path);

            using var invite = await host.SendAsAppAsync(HttpMethod.Post, InvitePath(playbackId), tvToken);
            Assert.AreEqual(HttpStatusCode.OK, invite.StatusCode, await invite.Content.ReadAsStringAsync());
            var invitation = (await invite.Content.ReadFromJsonAsync<CompanionInvitationV1>())!;
            Assert.IsTrue(invitation.ExpiresAt > DateTime.UtcNow);
            Assert.IsTrue(invitation.ExpiresAt < DateTime.UtcNow.AddMinutes(6));
            Assert.AreEqual(32, await host.ScalarAsync("SELECT octet_length(\"TokenHash\") FROM \"PlaybackCompanionInvitations\""));

            using var granted = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/companion/claims", new ClaimCompanionDataV1(invitation.Token));
            Assert.AreEqual(HttpStatusCode.OK, granted.StatusCode, await granted.Content.ReadAsStringAsync());
            var grant = (await granted.Content.ReadFromJsonAsync<CompanionGrantV1>())!;
            Assert.AreEqual(playbackId, grant.PlaybackSessionId);

            using var replay = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/companion/claims", new ClaimCompanionDataV1(invitation.Token));
            Assert.AreEqual(HttpStatusCode.NotFound, replay.StatusCode);

            using var browserInvite = await host.SendAsync(HttpMethod.Post, InvitePath(playbackId));
            Assert.AreEqual(HttpStatusCode.Forbidden, browserInvite.StatusCode);

            using var next = await host.SendAsAppAsync(HttpMethod.Post, InvitePath(playbackId), tvToken);
            var replacement = (await next.Content.ReadFromJsonAsync<CompanionInvitationV1>())!;
            await host.Fixture.RunSqlAsync("UPDATE \"PlaybackCompanionInvitations\" SET \"ExpiresAt\" = now() - interval '1 second'");
            using var expired = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/companion/claims", new ClaimCompanionDataV1(replacement.Token));
            Assert.AreEqual(HttpStatusCode.NotFound, expired.StatusCode);

            // A grant is tied to the controller's verified profile and the owner device's active session revision.
            await host.Fixture.RunSqlAsync("UPDATE \"AccountSessions\" SET \"RotationRevision\" = \"RotationRevision\" + 1 WHERE \"DevicePlatform\" = 'AndroidTV'");
            using var invalid = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/companion/" + grant.Id);
            Assert.AreEqual(HttpStatusCode.NotFound, invalid.StatusCode);
        }

        [TestMethod]
        public async Task Commands_AreRevisionProtectedSingleDispatchAndTvAcknowledged()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (tvToken, playbackId) = await PrepareTvAsync(host, storage.Path);
            var grant = await PairAsync(host, tvToken, playbackId);
            var companion = "/api/v1/playback/companion/" + grant.Id;

            using var heartbeat = await host.SendAsAppAsync(HttpMethod.Put, OwnerPath(playbackId) + "/state", tvToken,
                new CompanionReportStateDataV1(0, false, 0, 60_000));
            Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode, await heartbeat.Content.ReadAsStringAsync());

            using var initial = await host.SendAsync(HttpMethod.Get, companion);
            var initialState = (await initial.Content.ReadFromJsonAsync<CompanionStateV1>())!;
            Assert.AreEqual(0L, initialState.Revision);
            Assert.IsFalse(initialState.IsPlaying);

            var id = Guid.NewGuid();
            using var queued = await host.SendAsync(HttpMethod.Post, companion + "/commands", new QueueCompanionCommandDataV1(id, 0, "play", null));
            Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode, await queued.Content.ReadAsStringAsync());
            var command = (await queued.Content.ReadFromJsonAsync<QueuedCompanionCommandV1>())!;
            Assert.AreEqual(1L, command.Revision);

            using var unconfirmed = await host.SendAsync(HttpMethod.Get, companion);
            var waiting = (await unconfirmed.Content.ReadFromJsonAsync<CompanionStateV1>())!;
            Assert.IsFalse(waiting.IsPlaying);
            Assert.IsNull(waiting.LastCommandId);

            using var repeated = await host.SendAsync(HttpMethod.Post, companion + "/commands", new QueueCompanionCommandDataV1(id, 0, "play", null));
            Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
            Assert.AreEqual(command.Revision, (await repeated.Content.ReadFromJsonAsync<QueuedCompanionCommandV1>())!.Revision);

            using var stale = await host.SendAsync(HttpMethod.Post, companion + "/commands", new QueueCompanionCommandDataV1(Guid.NewGuid(), 0, "seek", 1200));
            Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);

            using var taken = await host.SendAsAppAsync(HttpMethod.Post, OwnerPath(playbackId) + "/next", tvToken);
            Assert.AreEqual(HttpStatusCode.OK, taken.StatusCode, await taken.Content.ReadAsStringAsync());
            var dispatched = (await taken.Content.ReadFromJsonAsync<CompanionPendingCommandV1>())!;
            Assert.AreEqual(id, dispatched.Id);
            Assert.AreEqual("play", dispatched.Action);

            using var empty = await host.SendAsAppAsync(HttpMethod.Post, OwnerPath(playbackId) + "/next", tvToken);
            Assert.IsNull((await empty.Content.ReadFromJsonAsync<CompanionPendingCommandV1>())!.Id);

            using var fakeHeartbeat = await host.SendAsAppAsync(HttpMethod.Put, OwnerPath(playbackId) + "/state", tvToken,
                new CompanionReportStateDataV1(1, true, 1000, 60_000));
            Assert.AreEqual(HttpStatusCode.BadRequest, fakeHeartbeat.StatusCode);

            using var ack = await host.SendAsAppAsync(HttpMethod.Put, OwnerPath(playbackId) + "/commands/" + id, tvToken,
                new CompanionAcknowledgeDataV1(1, true, true, 1200, 60_000));
            Assert.AreEqual(HttpStatusCode.OK, ack.StatusCode, await ack.Content.ReadAsStringAsync());
            using var doubleAck = await host.SendAsAppAsync(HttpMethod.Put, OwnerPath(playbackId) + "/commands/" + id, tvToken,
                new CompanionAcknowledgeDataV1(1, true, true, 1200, 60_000));
            Assert.AreEqual(HttpStatusCode.NotFound, doubleAck.StatusCode);

            using var confirmed = await host.SendAsync(HttpMethod.Get, companion);
            var state = (await confirmed.Content.ReadFromJsonAsync<CompanionStateV1>())!;
            Assert.AreEqual(id, state.LastCommandId);
            Assert.AreEqual(true, state.LastCommandSucceeded);
            Assert.IsTrue(state.IsPlaying);
            Assert.AreEqual(1200L, state.PositionMs);
            Assert.AreEqual(1L, await host.ScalarAsync("SELECT count(*) FROM \"PlaybackCompanionCommands\""));

            using var revoked = await host.SendAsync(HttpMethod.Delete, companion);
            Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode);
            using var afterRevoke = await host.SendAsync(HttpMethod.Get, companion);
            Assert.AreEqual(HttpStatusCode.NotFound, afterRevoke.StatusCode);
        }

        [TestMethod]
        public async Task EndAndProfileChange_RevokePhonePermission()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (tvToken, playbackId) = await PrepareTvAsync(host, storage.Path);
            var grant = await PairAsync(host, tvToken, playbackId);
            await host.Fixture.RunSqlAsync("UPDATE \"PlaybackSessions\" SET \"EndedAt\" = now() WHERE \"PublicId\" = '" + playbackId + "'");
            using var ended = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/companion/" + grant.Id);
            Assert.AreEqual(HttpStatusCode.NotFound, ended.StatusCode);

            using var second = await host.SendAsAppAsync(HttpMethod.Post, InvitePath(playbackId), tvToken);
            Assert.AreEqual(HttpStatusCode.NotFound, second.StatusCode);
        }


        [TestMethod]
        public async Task ControllerProfileChange_InvalidatesClaimAndCommands()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (tvToken, playbackId) = await PrepareTvAsync(host, storage.Path);
            var grant = await PairAsync(host, tvToken, playbackId);

            await host.Fixture.RunSqlAsync("""
                INSERT INTO "Profiles" ("Id", "OwnerAccountId", "DisplayName", "UiLocaleId")
                SELECT 901, 41, 'Other', "UiLocaleId" FROM "Profiles" WHERE "Id" = 101;
                INSERT INTO "AccountProfiles" ("AccountId", "ProfileId") VALUES (41, 901);
                UPDATE "AccountSessions"
                SET "ActiveProfileId" = 901
                WHERE "DevicePlatform" IS NULL AND "RevokedAt" IS NULL;
                """);

            using var denied = await host.SendAsync(HttpMethod.Get, "/api/v1/playback/companion/" + grant.Id);
            Assert.AreEqual(HttpStatusCode.NotFound, denied.StatusCode);
            using var command = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/companion/" + grant.Id + "/commands",
                new QueueCompanionCommandDataV1(Guid.NewGuid(), 0, "play", null));
            Assert.AreEqual(HttpStatusCode.NotFound, command.StatusCode);
        }

        [TestMethod]
        public async Task RevokedGrant_ReclaimCannotRevivePendingCommands()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (tvToken, playbackId) = await PrepareTvAsync(host, storage.Path);
            var grant = await PairAsync(host, tvToken, playbackId);
            using var heartbeat = await host.SendAsAppAsync(HttpMethod.Put, OwnerPath(playbackId) + "/state", tvToken,
                new CompanionReportStateDataV1(0, false, 0, 60_000));
            Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);

            using var queued = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/companion/" + grant.Id + "/commands",
                new QueueCompanionCommandDataV1(Guid.NewGuid(), 0, "play", null));
            Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode);

            using var revoke = await host.SendAsync(HttpMethod.Delete, "/api/v1/playback/companion/" + grant.Id);
            Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode);
            var revived = await PairAsync(host, tvToken, playbackId);
            Assert.AreEqual(grant.Id, revived.Id);

            using var next = await host.SendAsAppAsync(HttpMethod.Post, OwnerPath(playbackId) + "/next", tvToken);
            Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
            Assert.IsNull((await next.Content.ReadFromJsonAsync<CompanionPendingCommandV1>())!.Id);
        }


        [TestMethod]
        public async Task QueuedCommand_FromLoggedOutController_IsNeverDispatched()
        {
            using var storage = new LibraryTests.TemporaryDirectory();
            await using var host = await LibraryTests.CreateHostAsync(storage.Path);
            var (tvToken, playbackId) = await PrepareTvAsync(host, storage.Path);
            var grant = await PairAsync(host, tvToken, playbackId);

            using var heartbeat = await host.SendAsAppAsync(HttpMethod.Put, OwnerPath(playbackId) + "/state", tvToken,
                new CompanionReportStateDataV1(0, false, 0, 60_000));
            Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);

            using var queued = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/companion/" + grant.Id + "/commands",
                new QueueCompanionCommandDataV1(Guid.NewGuid(), 0, "play", null));
            Assert.AreEqual(HttpStatusCode.OK, queued.StatusCode);

            // Database-backed identity is checked a second time at TV dispatch, not only when the phone queued it.
            await host.Fixture.RunSqlAsync("""
                UPDATE "AccountSessions"
                SET "RevokedAt" = now()
                WHERE "DevicePlatform" IS NULL AND "RevokedAt" IS NULL;
                """);

            using var next = await host.SendAsAppAsync(HttpMethod.Post, OwnerPath(playbackId) + "/next", tvToken);
            Assert.AreEqual(HttpStatusCode.OK, next.StatusCode);
            Assert.IsNull((await next.Content.ReadFromJsonAsync<CompanionPendingCommandV1>())!.Id);
        }

        private static string OwnerPath(Guid id) => "/api/v1/playback/sessions/" + id + "/companion";
        private static string InvitePath(Guid id) => OwnerPath(id) + "/invitations";

        private static async Task<CompanionGrantV1> PairAsync(Host host, string tvToken, Guid playbackId)
        {
            using var invited = await host.SendAsAppAsync(HttpMethod.Post, InvitePath(playbackId), tvToken);
            Assert.AreEqual(HttpStatusCode.OK, invited.StatusCode, await invited.Content.ReadAsStringAsync());
            var invite = (await invited.Content.ReadFromJsonAsync<CompanionInvitationV1>())!;
            using var claimed = await host.SendAsync(HttpMethod.Post, "/api/v1/playback/companion/claims", new ClaimCompanionDataV1(invite.Token));
            Assert.AreEqual(HttpStatusCode.OK, claimed.StatusCode, await claimed.Content.ReadAsStringAsync());
            return (await claimed.Content.ReadFromJsonAsync<CompanionGrantV1>())!;
        }

        private static async Task<(string Token, Guid PlaybackId)> PrepareTvAsync(Host host, string storagePath)
        {
            var (assetId, _) = await PlaybackTests.CreatePlayableAsync(host, storagePath, wakeOnDemand: false);
            await host.Fixture.RunSqlAsync("UPDATE \"InstanceModules\" SET \"IsEnabled\" = TRUE WHERE \"Key\" = 'Devices'");
            using var started = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/pairings", new StartDevicePairingDataV1("Living room", "AndroidTV"));
            Assert.AreEqual(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
            var pairing = (await started.Content.ReadFromJsonAsync<DevicePairingStartedV1>())!;
            using var inspected = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals/inspection", new InspectDevicePairingDataV1(pairing.Code));
            Assert.AreEqual(HttpStatusCode.OK, inspected.StatusCode, await inspected.Content.ReadAsStringAsync());
            var request = (await inspected.Content.ReadFromJsonAsync<DevicePairingRequestV1>())!;
            using var approved = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/approvals", new ApproveDevicePairingDataV1(request.Id, pairing.Code));
            Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync());
            using var paired = await host.SendAsync(HttpMethod.Post, "/api/v1/devices/pairings/" + pairing.PairingId + "/claim");
            Assert.AreEqual(HttpStatusCode.OK, paired.StatusCode, await paired.Content.ReadAsStringAsync());
            var token = paired.Headers.GetValues(ServiceApi.DeviceTokenHeader).Single();

            using var startedPlayback = await host.SendAsAppAsync(HttpMethod.Post, "/api/v1/playback/assets/" + assetId + "/sessions", token, Browser);
            Assert.AreEqual(HttpStatusCode.OK, startedPlayback.StatusCode, await startedPlayback.Content.ReadAsStringAsync());
            var plan = (await startedPlayback.Content.ReadFromJsonAsync<PlaybackPlanV1>())!;
            return (token, plan.PlaybackSessionId);
        }
    }
}
