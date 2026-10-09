using Jularr.Web.Features.Playback.Decision;

namespace Jularr.Tests;

[TestClass]
public sealed class PlaybackPreparationDemandTests
{
    private static readonly PlaybackPreparationCapacity Available = new(
        AdminEnabled: true,
        InIdleWindow: true,
        InteractiveLoad: false,
        SourceAvailableWithoutWake: true,
        AlreadyHasCompatibleRendition: false,
        NeedsVideoConversion: true,
        CacheBytesAvailable: 10_000_000_000,
        DiskBytesAboveReserve: 10_000_000_000);

    private static readonly PlaybackPreparationCost Measured = new(
        Measured: true,
        PreparationMinutes: 20,
        SavedLiveEncodingMinutesPerView: 8,
        OutputBytes: 1_000_000_000);

    private static readonly PlaybackPreparationEvidence Popular = new(
        ResumeProfiles: 3,
        UpNextProfiles: 2,
        WatchlistProfiles: 4,
        RecentLiveTranscodes: 4);

    [TestMethod]
    public void RepeatedRealDemand_WithMeasuredBenefit_Eligible()
    {
        var result = PlaybackPreparationDemand.Evaluate(Popular, Measured, Available);

        Assert.AreEqual(PlaybackPreparationVerdict.Eligible, result.Verdict);
        Assert.IsTrue(result.ExpectedReuses > 5);
        Assert.IsTrue(result.ExpectedSavedEncoderMinutes > Measured.PreparationMinutes * 1.5);
    }

    [DataTestMethod]
    [DataRow(0, 0, 1, 0)]
    [DataRow(0, 0, 20, 0)]
    [DataRow(1, 0, 10, 1)]
    [DataRow(0, 1, 3, 1)]
    public void WeakSignals_NeverTriggerExpensiveEncoding(int resumes, int upNext, int watchlists, int transcodes)
    {
        var evidence = new PlaybackPreparationEvidence(resumes, upNext, watchlists, transcodes);
        Assert.AreEqual(
            PlaybackPreparationVerdict.DemandTooWeak,
            PlaybackPreparationDemand.Evaluate(evidence, Measured, Available).Verdict);
    }

    [DataTestMethod]
    [DataRow(false, true, false, true, false, true, PlaybackPreparationVerdict.Disabled)]
    [DataRow(true, false, false, true, false, true, PlaybackPreparationVerdict.NotIdle)]
    [DataRow(true, true, true, true, false, true, PlaybackPreparationVerdict.NotIdle)]
    [DataRow(true, true, false, false, false, true, PlaybackPreparationVerdict.SourceUnavailable)]
    [DataRow(true, true, false, true, true, true, PlaybackPreparationVerdict.AlreadyCompatible)]
    [DataRow(true, true, false, true, false, false, PlaybackPreparationVerdict.AlreadyCompatible)]
    public void AdmissionPolicy_BlocksUnsafePreparation(
        bool enabled,
        bool idle,
        bool busy,
        bool available,
        bool hasVariant,
        bool needsConversion,
        PlaybackPreparationVerdict expected)
    {
        var capacity = Available with
        {
            AdminEnabled = enabled,
            InIdleWindow = idle,
            InteractiveLoad = busy,
            SourceAvailableWithoutWake = available,
            AlreadyHasCompatibleRendition = hasVariant,
            NeedsVideoConversion = needsConversion
        };
        Assert.AreEqual(expected, PlaybackPreparationDemand.Evaluate(Popular, Measured, capacity).Verdict);
    }

    [TestMethod]
    public void ResourceAndCostLimits_RejectLowReturnOrUnknownEstimates()
    {
        Assert.AreEqual(
            PlaybackPreparationVerdict.InsufficientSpace,
            PlaybackPreparationDemand.Evaluate(
                Popular, Measured, Available with { DiskBytesAboveReserve = 100 }).Verdict);

        Assert.AreEqual(
            PlaybackPreparationVerdict.CostUnknown,
            PlaybackPreparationDemand.Evaluate(
                Popular, Measured with { Measured = false }, Available).Verdict);

        Assert.AreEqual(
            PlaybackPreparationVerdict.CostUnknown,
            PlaybackPreparationDemand.Evaluate(
                Popular, Measured with { PreparationMinutes = double.NaN }, Available).Verdict);

        Assert.AreEqual(
            PlaybackPreparationVerdict.InsufficientBenefit,
            PlaybackPreparationDemand.Evaluate(
                Popular, Measured with { PreparationMinutes = 1_000 }, Available).Verdict);
    }

    [TestMethod]
    public void RepeatedRealTranscodes_QualifyWithoutWatchlistInflation()
    {
        var evidence = new PlaybackPreparationEvidence(0, 0, 0, 4);
        var cost = Measured with { PreparationMinutes = 10, SavedLiveEncodingMinutesPerView = 12 };
        var result = PlaybackPreparationDemand.Evaluate(evidence, cost, Available);

        Assert.AreEqual(PlaybackPreparationVerdict.Eligible, result.Verdict);
        Assert.AreEqual(2, result.ExpectedReuses);
    }
}
