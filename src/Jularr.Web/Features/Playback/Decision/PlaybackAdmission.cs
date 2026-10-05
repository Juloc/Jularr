using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>Why a delivery was not admitted. Each code reaches the client as the API error code and has a <c>playback.reason.*</c> text.</summary>
public static class PlaybackAdmissionCodes
{
    public const string TranscodingDisabled = "transcoding_disabled";
    public const string TranscoderBusy = "transcoder_busy";
    public const string CacheBudgetExhausted = "cache_budget_exhausted";
    public const string CacheFreeSpaceLow = "cache_free_space_low";

    /// <summary>The API error text of a refusal; the code is the machine-readable part clients translate.</summary>
    public static string Message(string code) =>
        code switch
        {
            TranscodingDisabled => "Server transcoding is turned off.",
            TranscoderBusy => "Every server transcode slot is in use.",
            CacheBudgetExhausted => "The server playback cache is full.",
            CacheFreeSpaceLow => "The server playback cache volume is low on free space.",
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
}

/// <summary>
/// The outcome of asking the server to start one delivery: the encoder it will use and the slot
/// it holds, or an explicit refusal. The lease belongs to whoever starts the delivery.
/// </summary>
public sealed record PlaybackAdmission(PlaybackCostClass CostClass, PlaybackEncoderTarget Encoder, IDisposable? Lease, string? RefusalCode)
{
    public bool Admitted => RefusalCode is null;
}

/// <summary>A refused admission reaching a caller that cannot return it (HLS start deep inside the session manager).</summary>
public sealed class PlaybackAdmissionRefusedException(string code) : InvalidOperationException($"Playback delivery was refused: {code}.")
{
    public string Code { get; } = code;
}

/// <summary>Why a started delivery failed: a stable <see cref="Reason"/> code the plan text and Admin map, and the bounded raw <see cref="Detail"/> for Admin only.</summary>
public sealed record PlaybackStartFailure(string Reason, string? Detail = null)
{
    public const string StartFailed = "start_failed";
    public const string StartTimedOut = "start_timed_out";
}

public static class PlaybackCostClasses
{
    public static PlaybackCostClass For(PlaybackPlan plan, PlaybackEncoderTarget encoder)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(encoder);
        return plan.Video switch
        {
            null => PlaybackCostClass.AudioOnly,
            { Copy: true } => PlaybackCostClass.Remux,
            _ => encoder.IsHardware ? PlaybackCostClass.HardwareVideo : PlaybackCostClass.SoftwareVideo
        };
    }
}

/// <summary>
/// Admission of a delivery against the server resource policy: the Admin's transcoding switch,
/// the encoder the breaker allows and the slot limit of the delivery's cost class. The HLS cache
/// budget is checked by the cache owner (<see cref="HlsPlaybackSessionManager"/>) when the
/// session directory is created.
/// </summary>
public sealed class PlaybackAdmissionService(PlaybackTranscodingSettingsStore settings, PlaybackTranscodeSlots slots, PlaybackHardwareService hardware)
{
    public PlaybackAdmission Admit(PlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var encoder = plan.TranscodesVideo ? hardware.Resolve(plan.Video!.Encoder) : PlaybackEncoderTarget.Software;
        var costClass = PlaybackCostClasses.For(plan, encoder);
        if (plan.TranscodesVideo && !settings.Current.TranscodingEnabled)
        {
            return new PlaybackAdmission(costClass, encoder, null, PlaybackAdmissionCodes.TranscodingDisabled);
        }

        var lease = slots.TryAcquire(costClass);
        return new PlaybackAdmission(costClass, encoder, lease, lease is null ? PlaybackAdmissionCodes.TranscoderBusy : null);
    }

    /// <summary>
    /// A software admission for a session whose hardware start failed. The failed attempt already
    /// released its slot, so this takes a software slot instead; the admission is refused when none is free.
    /// </summary>
    public PlaybackAdmission AdmitSoftwareFallback()
    {
        var lease = slots.TryAcquire(PlaybackCostClass.SoftwareVideo);
        return new PlaybackAdmission(PlaybackCostClass.SoftwareVideo, PlaybackEncoderTarget.Software, lease, lease is null ? PlaybackAdmissionCodes.TranscoderBusy : null);
    }

    /// <summary>Counts a hardware start toward the breaker (a null failure is a success); software starts never open a breaker.</summary>
    public void ReportStart(PlaybackAdmission admission, PlaybackStartFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!admission.Encoder.IsHardware)
        {
            return;
        }

        if (failure is null)
        {
            hardware.Breaker.RecordSuccess(admission.Encoder.Backend);
        }
        else
        {
            hardware.Breaker.RecordFailure(admission.Encoder.Backend, failure.Reason, failure.Detail);
        }
    }
}
