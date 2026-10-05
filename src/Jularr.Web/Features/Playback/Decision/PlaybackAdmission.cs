using Jularr.Web.Features.Playback.Transcoding;

namespace Jularr.Web.Features.Playback.Decision;

/// <summary>Why a delivery was not admitted. Each code reaches the client as the API error code and has a <c>playback.reason.*</c> text.</summary>
public static class PlaybackAdmissionCodes
{
    public const string TranscodingDisabled = "transcoding_disabled";
    public const string TranscoderBusy = "transcoder_busy";
    public const string CacheBudgetExhausted = "cache_budget_exhausted";
    public const string CacheFreeSpaceLow = "cache_free_space_low";
    public const string CacheFolderNotOwned = "cache_folder_not_owned";
    public const string ProfileSessionLimit = "profile_session_limit";

    /// <summary>The API error text of a refusal; the code is the machine-readable part clients translate.</summary>
    public static string Message(string code) =>
        code switch
        {
            TranscodingDisabled => "Server transcoding is turned off.",
            TranscoderBusy => "Every server transcode slot is in use.",
            CacheBudgetExhausted => "The server playback cache is full.",
            CacheFreeSpaceLow => "The server playback cache volume is low on free space.",
            CacheFolderNotOwned => "The server playback cache folder holds files that are not Jularr's.",
            ProfileSessionLimit => "This profile already runs the most playback sessions it may.",
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
/// the encoder the breaker allows, the slot limit of the delivery's cost class and the per-profile
/// cap. Every ffmpeg-spawning route, plan-based or legacy, goes through here so the Admin limits
/// are real. The HLS cache budget is checked by the cache owner (<see cref="HlsPlaybackSessionManager"/>)
/// when the session directory is created.
/// </summary>
public sealed class PlaybackAdmissionService(PlaybackTranscodingSettingsStore settings, PlaybackTranscodeSlots slots, PlaybackHardwareService hardware)
{
    public PlaybackAdmission Admit(PlaybackPlan plan, string profileId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var encoder = plan.TranscodesVideo ? hardware.Resolve(plan.Video!.Encoder) : PlaybackEncoderTarget.Software;
        return AdmitAttempt(plan, profileId, encoder);
    }

    /// <summary>An admission for a legacy delivery that has no plan: a software transcode or a remux.</summary>
    public PlaybackAdmission AdmitLegacy(PlaybackCostClass costClass, string profileId)
    {
        var transcodes = costClass is PlaybackCostClass.SoftwareVideo or PlaybackCostClass.HardwareVideo;
        return Acquire(costClass, PlaybackEncoderTarget.Software, profileId, transcodes);
    }

    /// <summary>
    /// Runs one delivery start with the automatic fallbacks and the breaker accounting. A hardware start that
    /// fails is first retried on the same encoder with software decoding when it decoded on the device (a
    /// device that encodes but cannot decode must not lose its encoder), then on software. Only a failure
    /// that the software fallback of the same request then survives counts against the backend: when
    /// software fails too, the source or the environment is at fault and nothing is recorded. A timeout is never evidence
    /// against a device (a sleeping disk or a slow source looks identical): a hardware timeout is retried on software
    /// and charges nothing; a timeout of the software attempt, a refusal or an invalid argument set is never retried or counted. <paramref name="start"/>
    /// owns the admission's lease and must release it when the attempt fails.
    /// </summary>
    public async Task<TResult> StartAsync<TResult>(PlaybackPlan plan, string profileId, Func<PlaybackAdmission, Task<TResult>> start)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(start);
        var admitted = Admit(plan, profileId);
        PlaybackHardwareBackend failedBackend = PlaybackHardwareBackend.Software;
        string? hardwareFailure = null;
        var decodeSuspected = false;
        while (true)
        {
            if (!admitted.Admitted)
            {
                throw new PlaybackAdmissionRefusedException(admitted.RefusalCode!);
            }

            try
            {
                var result = await start(admitted);
                Settle(admitted, decodeSuspected, failedBackend, hardwareFailure);
                return result;
            }
            catch (Exception exception) when (admitted.Encoder.IsHardware && exception is (InvalidOperationException and not PlaybackAdmissionRefusedException) or System.ComponentModel.Win32Exception or TimeoutException)
            {
                PlaybackEncoderTarget next;
                if (exception is TimeoutException)
                {
                    // Silence says nothing about the device: a sleeping disk or a slow source looks the same. Software gets its try, nothing is charged.
                    next = PlaybackEncoderTarget.Software;
                }
                else if (PlaybackDeliveryCommand.UsesHardwareDecoding(admitted.Encoder, plan.Video!))
                {
                    decodeSuspected = true;
                    next = admitted.Encoder with { HardwareDecoding = false };
                }
                else
                {
                    failedBackend = admitted.Encoder.Backend;
                    hardwareFailure = exception.Message;
                    next = PlaybackEncoderTarget.Software;
                }

                // The failed attempt released its slot; the next one needs its own.
                admitted = AdmitAttempt(plan, profileId, next);
            }
        }
    }

    private PlaybackAdmission AdmitAttempt(PlaybackPlan plan, string profileId, PlaybackEncoderTarget encoder) =>
        Acquire(PlaybackCostClasses.For(plan, encoder), encoder, profileId, plan.TranscodesVideo);

    private PlaybackAdmission Acquire(PlaybackCostClass costClass, PlaybackEncoderTarget encoder, string profileId, bool transcodes)
    {
        if (transcodes && !settings.Current.TranscodingEnabled)
        {
            return new PlaybackAdmission(costClass, encoder, null, PlaybackAdmissionCodes.TranscodingDisabled);
        }

        var lease = slots.TryAcquire(costClass, profileId);
        string? refusal = null;
        if (lease is null)
        {
            refusal = slots.ActiveFor(profileId) >= PlaybackTranscodeSlots.MaxPerProfile ? PlaybackAdmissionCodes.ProfileSessionLimit : PlaybackAdmissionCodes.TranscoderBusy;
        }

        return new PlaybackAdmission(costClass, encoder, lease, refusal);
    }

    // What a successful start proves about the backends involved.
    private void Settle(PlaybackAdmission succeeded, bool decodeSuspected, PlaybackHardwareBackend failedBackend, string? hardwareFailure)
    {
        if (succeeded.Encoder.IsHardware)
        {
            if (decodeSuspected)
            {
                hardware.DisableHardwareDecoding(succeeded.Encoder.Backend);
            }

            hardware.Breaker.RecordSuccess(succeeded.Encoder.Backend);
        }
        else if (hardwareFailure is not null)
        {
            hardware.Breaker.RecordFailure(failedBackend, hardwareFailure);
        }
    }
}
