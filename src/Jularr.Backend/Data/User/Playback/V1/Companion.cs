using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.User.Playback.V1
{
    public sealed class CompanionInvitationV1 : BaseResponse
    {
        public CompanionInvitationV1(string token, DateTime expiresAt)
        {
            Token = token;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Token))]
        public string Token { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }
    }

    public sealed class ClaimCompanionDataV1 : BaseData
    {
        public ClaimCompanionDataV1(string token)
        {
            Token = token;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Token))]
        public string Token { get; }
    }

    public sealed class CompanionGrantParametersV1 : BaseParameters
    {
        public CompanionGrantParametersV1(Guid companionId)
        {
            CompanionId = companionId;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid CompanionId { get; }
    }

    public sealed class CompanionGrantV1 : BaseResponse
    {
        public CompanionGrantV1(Guid id, Guid playbackSessionId)
        {
            Id = id;
            PlaybackSessionId = playbackSessionId;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid Id { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }
    }

    public sealed class CompanionStateV1 : BaseResponse
    {
        public CompanionStateV1(Guid playbackSessionId, long revision, bool isPlaying, long positionMs, long? durationMs, DateTime? reportedAt,
            Guid? lastCommandId, bool? lastCommandSucceeded)
        {
            PlaybackSessionId = playbackSessionId;
            Revision = revision;
            IsPlaying = isPlaying;
            PositionMs = positionMs;
            DurationMs = durationMs;
            ReportedAt = reportedAt;
            LastCommandId = lastCommandId;
            LastCommandSucceeded = lastCommandSucceeded;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Revision))]
        public long Revision { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.IsPlaying))]
        public bool IsPlaying { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs))]
        public long PositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.DurationMs), false)]
        public long? DurationMs { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.ReportedAt), false)]
        public DateTime? ReportedAt { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id), false)]
        public Guid? LastCommandId { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Succeeded), false)]
        public bool? LastCommandSucceeded { get; }
    }

    public sealed class QueueCompanionCommandDataV1 : BaseData
    {
        public QueueCompanionCommandDataV1(Guid commandId, long expectedRevision, string action, long? positionMs)
        {
            CommandId = commandId;
            ExpectedRevision = expectedRevision;
            Action = action;
            PositionMs = positionMs;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid CommandId { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Revision))]
        public long ExpectedRevision { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Action))]
        public string Action { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs), false)]
        public long? PositionMs { get; }

        public override void Validate()
        {
            base.Validate();
            if (CommandId == Guid.Empty || Action is not ("play" or "pause" or "seek") || (Action == "seek") != PositionMs.HasValue)
            {
                throw new DataValidationException(nameof(Action), "invalid_transition");
            }
        }
    }

    public sealed class QueuedCompanionCommandV1 : BaseResponse
    {
        public QueuedCompanionCommandV1(Guid id, long revision)
        {
            Id = id;
            Revision = revision;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid Id { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.CommandRevision))]
        public long Revision { get; }
    }

    public sealed class CompanionCommandParametersV1 : BaseParameters
    {
        public CompanionCommandParametersV1(Guid playbackSessionId, Guid commandId)
        {
            PlaybackSessionId = playbackSessionId;
            CommandId = commandId;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid CommandId { get; }
    }

    public sealed class CompanionPendingCommandV1 : BaseResponse
    {
        public CompanionPendingCommandV1(Guid? id, long? revision, string? action, long? positionMs)
        {
            Id = id;
            Revision = revision;
            Action = action;
            PositionMs = positionMs;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id), false)]
        public Guid? Id { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.CommandRevision), false)]
        public long? Revision { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Action), false)]
        public string? Action { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs), false)]
        public long? PositionMs { get; }
    }

    public sealed class CompanionReportStateDataV1 : BaseData
    {
        public CompanionReportStateDataV1(long revision, bool isPlaying, long positionMs, long? durationMs)
        {
            Revision = revision;
            IsPlaying = isPlaying;
            PositionMs = positionMs;
            DurationMs = durationMs;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Revision))]
        public long Revision { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.IsPlaying))]
        public bool IsPlaying { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs))]
        public long PositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.DurationMs), false)]
        public long? DurationMs { get; }
    }

    public sealed class CompanionAcknowledgeDataV1 : BaseData
    {
        public CompanionAcknowledgeDataV1(long revision, bool succeeded, bool isPlaying, long positionMs, long? durationMs)
        {
            Revision = revision;
            Succeeded = succeeded;
            IsPlaying = isPlaying;
            PositionMs = positionMs;
            DurationMs = durationMs;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.CommandRevision))]
        public long Revision { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Succeeded))]
        public bool Succeeded { get; }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.IsPlaying))]
        public bool IsPlaying { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PositionMs))]
        public long PositionMs { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.DurationMs), false)]
        public long? DurationMs { get; }
    }
    public sealed class ClaimCompanionParametersV1 : BaseParameters
    {
    }

}
