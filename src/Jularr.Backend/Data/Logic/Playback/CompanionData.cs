using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Account;
using Jularr.Backend.Data.Fields.Playback;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Playback
{
    internal sealed class CompanionOwnerData : BaseData
    {
        public CompanionOwnerData(Guid playbackSessionId, Guid ownerPublicId, long ownerRevision, long profileId)
        {
            PlaybackSessionId = playbackSessionId;
            OwnerPublicId = ownerPublicId;
            OwnerRevision = ownerRevision;
            ProfileId = profileId;
        }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid OwnerPublicId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.RotationRevision))]
        public long OwnerRevision { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    internal sealed class CompanionControllerData : BaseData
    {
        public CompanionControllerData(Guid companionId, Guid controllerPublicId, long controllerRevision, long accountId, long profileId)
        {
            CompanionId = companionId;
            ControllerPublicId = controllerPublicId;
            ControllerRevision = controllerRevision;
            AccountId = accountId;
            ProfileId = profileId;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid CompanionId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid ControllerPublicId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.RotationRevision))]
        public long ControllerRevision { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.AccountId))]
        public long AccountId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    internal sealed class CompanionClaimData : BaseData
    {
        public CompanionClaimData(byte[] tokenHash, Guid controllerPublicId, long controllerRevision, long accountId, long profileId)
        {
            TokenHash = tokenHash;
            ControllerPublicId = controllerPublicId;
            ControllerRevision = controllerRevision;
            AccountId = accountId;
            ProfileId = profileId;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.TokenHash))]
        public byte[] TokenHash { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.PublicId))]
        public Guid ControllerPublicId { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.RotationRevision))]
        public long ControllerRevision { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.AccountId))]
        public long AccountId { get; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long ProfileId { get; }
    }

    internal sealed class CompanionTokenHashData : BaseData
    {
        public CompanionTokenHashData(byte[] tokenHash, DateTime expiresAt)
        {
            TokenHash = tokenHash;
            ExpiresAt = expiresAt;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.TokenHash))]
        public byte[] TokenHash { get; }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.ExpiresAt))]
        public DateTime ExpiresAt { get; }
    }

    internal sealed class CompanionAccessRow : BaseData
    {
        public CompanionAccessRow(long id, Guid playbackSessionId)
        {
            Id = id;
            PlaybackSessionId = playbackSessionId;
        }

        [DataField(typeof(AccountSessionFields), nameof(AccountSessionFields.Id))]
        public long Id { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }
    }

    internal sealed class CompanionGrantRow : BaseData
    {
        public CompanionGrantRow(Guid id, Guid playbackSessionId)
        {
            Id = id;
            PlaybackSessionId = playbackSessionId;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid Id { get; }

        [DataField(typeof(PlaybackFields), nameof(PlaybackFields.PlaybackSessionId))]
        public Guid PlaybackSessionId { get; }
    }

    internal sealed class CompanionCommandData : BaseData
    {
        public CompanionCommandData(Guid commandId, long expectedRevision, string action, long? positionMs)
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
    }

    internal sealed class CompanionStateData : BaseData
    {
        public CompanionStateData(long revision, bool isPlaying, long positionMs, long? durationMs)
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

    internal sealed class CompanionAckData : BaseData
    {
        public CompanionAckData(Guid commandId, long revision, bool succeeded, bool isPlaying, long positionMs, long? durationMs)
        {
            CommandId = commandId;
            Revision = revision;
            Succeeded = succeeded;
            IsPlaying = isPlaying;
            PositionMs = positionMs;
            DurationMs = durationMs;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid CommandId { get; }

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

    internal sealed class CompanionRevisionRow : BaseData
    {
        public CompanionRevisionRow(long revision)
        {
            Revision = revision;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Revision))]
        public long Revision { get; }
    }

    internal sealed class CompanionStatusRow : BaseData
    {
        public CompanionStatusRow(Guid playbackSessionId, long revision, bool isPlaying, long positionMs, long? durationMs, DateTime? reportedAt,
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

    internal sealed class CompanionNextCommandRow : BaseData
    {
        public CompanionNextCommandRow(Guid? id, long? revision, string? action, long? positionMs)
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
    internal sealed class CompanionGrantIdData : BaseData
    {
        public CompanionGrantIdData(Guid grantId)
        {
            GrantId = grantId;
        }

        [DataField(typeof(CompanionFields), nameof(CompanionFields.Id))]
        public Guid GrantId { get; }
    }

}
