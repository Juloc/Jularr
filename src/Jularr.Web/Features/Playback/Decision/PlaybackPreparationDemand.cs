namespace Jularr.Web.Features.Playback.Decision;

public sealed record PlaybackPreparationEvidence(
    int ResumeProfiles,
    int UpNextProfiles,
    int WatchlistProfiles,
    int RecentLiveTranscodes);

public sealed record PlaybackPreparationCost(
    bool Measured,
    double PreparationMinutes,
    double SavedLiveEncodingMinutesPerView,
    long OutputBytes);

public sealed record PlaybackPreparationCapacity(
    bool AdminEnabled,
    bool InIdleWindow,
    bool InteractiveLoad,
    bool SourceAvailableWithoutWake,
    bool AlreadyHasCompatibleRendition,
    bool NeedsVideoConversion,
    long CacheBytesAvailable,
    long DiskBytesAboveReserve);

public enum PlaybackPreparationVerdict
{
    Disabled,
    NotIdle,
    SourceUnavailable,
    AlreadyCompatible,
    DemandTooWeak,
    CostUnknown,
    InsufficientSpace,
    InsufficientBenefit,
    Eligible
}

public sealed record PlaybackPreparationAssessment(
    PlaybackPreparationVerdict Verdict,
    double ExpectedReuses,
    double ExpectedSavedEncoderMinutes);

public static class PlaybackPreparationDemand
{
    public static PlaybackPreparationAssessment Evaluate(
        PlaybackPreparationEvidence demand,
        PlaybackPreparationCost cost,
        PlaybackPreparationCapacity capacity)
    {
        if (!capacity.AdminEnabled)
        {
            return new(PlaybackPreparationVerdict.Disabled, 0, 0);
        }

        if (!capacity.InIdleWindow || capacity.InteractiveLoad)
        {
            return new(PlaybackPreparationVerdict.NotIdle, 0, 0);
        }

        if (!capacity.SourceAvailableWithoutWake)
        {
            return new(PlaybackPreparationVerdict.SourceUnavailable, 0, 0);
        }

        if (capacity.AlreadyHasCompatibleRendition || !capacity.NeedsVideoConversion)
        {
            return new(PlaybackPreparationVerdict.AlreadyCompatible, 0, 0);
        }

        if (demand.ResumeProfiles < 0 || demand.UpNextProfiles < 0 ||
            demand.WatchlistProfiles < 0 || demand.RecentLiveTranscodes < 0)
        {
            return new(PlaybackPreparationVerdict.DemandTooWeak, 0, 0);
        }

        var independentSignals = demand.ResumeProfiles + demand.UpNextProfiles;
        if (independentSignals < 2 && demand.RecentLiveTranscodes < 3)
        {
            return new(PlaybackPreparationVerdict.DemandTooWeak, 0, 0);
        }

        if (!cost.Measured ||
            !double.IsFinite(cost.PreparationMinutes) || cost.PreparationMinutes <= 0 ||
            !double.IsFinite(cost.SavedLiveEncodingMinutesPerView) ||
            cost.SavedLiveEncodingMinutesPerView <= 0 ||
            cost.OutputBytes <= 0)
        {
            return new(PlaybackPreparationVerdict.CostUnknown, 0, 0);
        }

        var expectedReuses =
            demand.ResumeProfiles * 0.8 +
            demand.UpNextProfiles * 0.6 +
            Math.Min(demand.RecentLiveTranscodes, 12) * 0.5 +
            Math.Min(Math.Max(0, demand.WatchlistProfiles - 1), 5) * 0.1;

        if (cost.OutputBytes > capacity.CacheBytesAvailable ||
            cost.OutputBytes > capacity.DiskBytesAboveReserve)
        {
            return new(PlaybackPreparationVerdict.InsufficientSpace, expectedReuses, 0);
        }

        var saved = expectedReuses * cost.SavedLiveEncodingMinutesPerView;
        return expectedReuses >= 1.5 && saved >= cost.PreparationMinutes * 1.5
            ? new(PlaybackPreparationVerdict.Eligible, expectedReuses, saved)
            : new(PlaybackPreparationVerdict.InsufficientBenefit, expectedReuses, saved);
    }
}
