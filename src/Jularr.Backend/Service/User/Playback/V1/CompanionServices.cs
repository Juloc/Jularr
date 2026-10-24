using System.Security;
using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Playback.V1
{
    [ServiceEndpoint("POST", "/api/v1/playback/sessions/{playbackSessionId:guid}/companion/invitations", RateLimit = "companion-claim")]
    public sealed class CreateCompanionInvitationV1 : UserService<PlaybackSessionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CompanionInvitationV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnCreate()
        {
            RequireTv();
            var (token, expiresAt) = await Context.CreateCompanionInvitationAsync(Owner(Parameters.PlaybackSessionId), CancellationToken);
            return new CompanionInvitationV1(token, expiresAt);
        }

        private void RequireTv()
        {
            if (!Auth.IsDevice)
            {
                throw new SecurityException("Only a paired TV can invite a remote.");
            }
        }

        private CompanionOwnerData Owner(Guid playbackSessionId) =>
            new(playbackSessionId, Auth.SessionPublicId!.Value, Auth.SessionRevision!.Value, Auth.ProfileId!.Value);
    }

    [ServiceEndpoint("POST", "/api/v1/playback/companion/claims", RateLimit = "companion-claim")]
    public sealed class ClaimCompanionV1 : UserService<ClaimCompanionParametersV1, ClaimCompanionDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CompanionGrantV1>()];
        protected override ModuleRequirement GetInstanceModules(ClaimCompanionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(ClaimCompanionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(Data.Token));
            var claimed = await Context.ClaimCompanionAsync(new CompanionClaimData(tokenHash, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.AccountId!.Value, Auth.ProfileId!.Value), CancellationToken);
            return new CompanionGrantV1(claimed.Id, claimed.PlaybackSessionId);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/companion/{companionId:guid}", RateLimit = "companion-read")]
    public sealed class ReadCompanionStateV1 : UserService<CompanionGrantParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CompanionStateV1>()];
        protected override ModuleRequirement GetInstanceModules(CompanionGrantParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(CompanionGrantParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            var state = await Context.ReadCompanionStateAsync(new CompanionControllerData(Parameters.CompanionId, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.AccountId!.Value, Auth.ProfileId!.Value), CancellationToken);
            return new CompanionStateV1(state.PlaybackSessionId, state.Revision, state.IsPlaying, state.PositionMs, state.DurationMs, state.ReportedAt,
                state.LastCommandId, state.LastCommandSucceeded);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/playback/companion/{companionId:guid}/commands", RateLimit = "companion-command")]
    public sealed class QueueCompanionCommandV1 : UserService<CompanionGrantParametersV1, QueueCompanionCommandDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<QueuedCompanionCommandV1>()];
        protected override ModuleRequirement GetInstanceModules(CompanionGrantParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(CompanionGrantParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnCreate()
        {
            var revision = await Context.QueueCompanionCommandAsync(new CompanionControllerData(Parameters.CompanionId, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.AccountId!.Value, Auth.ProfileId!.Value),
                new CompanionCommandData(Data.CommandId, Data.ExpectedRevision, Data.Action, Data.PositionMs), CancellationToken);
            return new QueuedCompanionCommandV1(Data.CommandId, revision);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/playback/companion/{companionId:guid}")]
    public sealed class RevokeCompanionV1 : UserService<CompanionGrantParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CompanionGrantParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(CompanionGrantParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnDelete()
        {
            await Context.RevokeCompanionAsync(new CompanionControllerData(Parameters.CompanionId, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.AccountId!.Value, Auth.ProfileId!.Value), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/sessions/{playbackSessionId:guid}/companion")]
    public sealed class ReadTvCompanionStateV1 : UserService<PlaybackSessionParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CompanionStateV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            if (!Auth.IsDevice) throw new SecurityException("Only the TV can report this playback.");
            var state = await Context.ReadCompanionOwnerStateAsync(new CompanionOwnerData(Parameters.PlaybackSessionId, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.ProfileId!.Value), CancellationToken);
            return new CompanionStateV1(state.PlaybackSessionId, state.Revision, state.IsPlaying, state.PositionMs, state.DurationMs, state.ReportedAt,
                state.LastCommandId, state.LastCommandSucceeded);
        }
    }

    [ServiceEndpoint("POST", "/api/v1/playback/sessions/{playbackSessionId:guid}/companion/next", RateLimit = "companion-read")]
    public sealed class TakeNextCompanionCommandV1 : UserService<PlaybackSessionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Create;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<CompanionPendingCommandV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnCreate()
        {
            if (!Auth.IsDevice) throw new SecurityException("Only the TV can receive remote commands.");
            var command = await Context.TakeNextCompanionCommandAsync(new CompanionOwnerData(Parameters.PlaybackSessionId, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.ProfileId!.Value), CancellationToken);
            return new CompanionPendingCommandV1(command?.Id, command?.Revision, command?.Action, command?.PositionMs);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/playback/sessions/{playbackSessionId:guid}/companion/state", RateLimit = "companion-read")]
    public sealed class ReportTvCompanionStateV1 : UserService<PlaybackSessionParametersV1, CompanionReportStateDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            if (!Auth.IsDevice) throw new SecurityException("Only the TV can report playback state.");
            await Context.ReportCompanionStateAsync(new CompanionOwnerData(Parameters.PlaybackSessionId, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.ProfileId!.Value),
                new CompanionStateData(Data.Revision, Data.IsPlaying, Data.PositionMs, Data.DurationMs), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("PUT", "/api/v1/playback/sessions/{playbackSessionId:guid}/companion/commands/{commandId:guid}")]
    public sealed class AcknowledgeTvCompanionCommandV1 : UserService<CompanionCommandParametersV1, CompanionAcknowledgeDataV1>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Update;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(CompanionCommandParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(CompanionCommandParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnUpdate()
        {
            if (!Auth.IsDevice) throw new SecurityException("Only the TV can acknowledge remote commands.");
            await Context.AcknowledgeCompanionCommandAsync(new CompanionOwnerData(Parameters.PlaybackSessionId, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.ProfileId!.Value),
                new CompanionAckData(Parameters.CommandId, Data.Revision, Data.Succeeded, Data.IsPlaying, Data.PositionMs, Data.DurationMs), CancellationToken);
            return new UpdateResult(true);
        }
    }

    [ServiceEndpoint("DELETE", "/api/v1/playback/sessions/{playbackSessionId:guid}/companion")]
    public sealed class RevokeTvCompanionsV1 : UserService<PlaybackSessionParametersV1, NoData>
    {
        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Delete;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSessionParametersV1 parameters) => ModuleRequirement.All("Library", "Playback");
        protected override ServicePermission GetPermission(PlaybackSessionParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnDelete()
        {
            if (!Auth.IsDevice) throw new SecurityException("Only the TV can revoke its companions.");
            await Context.RevokePlaybackCompanionsAsync(new CompanionOwnerData(Parameters.PlaybackSessionId, Auth.SessionPublicId!.Value,
                Auth.SessionRevision!.Value, Auth.ProfileId!.Value), CancellationToken);
            return new UpdateResult(true);
        }
    }
}
