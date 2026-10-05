using System.Text.Json;
using Jularr.Web.Data;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Localization;
using Jularr.Web.Features.Playback.Decision;
using Jularr.Web.Features.Playback.Transcoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

/// <summary>
/// Owner-only server resource policy of playback: whether the server may transcode, how many
/// sessions each cost class may run, and the HLS cache folder and limits. The page owns no rules:
/// <see cref="PlaybackTranscodingSettingsStore"/> validates and stores.
/// </summary>
[Authorize(Policy = JularrPolicies.AdminSystem)]
public sealed class TranscodingModel(AppDbContext db, PlaybackTranscodingSettingsStore store, PlaybackTranscodeSlots slots) : PageModel
{
    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    [BindProperty]
    public bool TranscodingEnabled { get; set; }

    [BindProperty]
    public int SoftwareVideoSessions { get; set; }

    [BindProperty]
    public int HardwareVideoSessions { get; set; }

    [BindProperty]
    public int RemuxSessions { get; set; }

    [BindProperty]
    public int AudioOnlySessions { get; set; }

    [BindProperty]
    public string HlsCachePath { get; set; } = "";

    [BindProperty]
    public long CacheBudgetGiB { get; set; }

    [BindProperty]
    public long FreeSpaceFloorGiB { get; set; }

    [BindProperty]
    public PlaybackBufferPreset BufferPreset { get; set; }

    public int Running(PlaybackCostClass costClass) => slots.Active(costClass);

    /// <summary>The value of one per-class session field, addressed by the form field name the page posts.</summary>
    public int SessionLimit(string field) =>
        field switch
        {
            nameof(SoftwareVideoSessions) => SoftwareVideoSessions,
            nameof(HardwareVideoSessions) => HardwareVideoSessions,
            nameof(RemuxSessions) => RemuxSessions,
            nameof(AudioOnlySessions) => AudioOnlySessions,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a session limit field.")
        };

    public string? FieldError(string field) => ModelState.TryGetValue(field, out var entry) ? entry.Errors.FirstOrDefault()?.ErrorMessage : null;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        try
        {
            // A read for display: what the server enforces (store.Current) is not touched by opening the page.
            Fill(await store.ReadStoredAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The broken or unreadable file is not reset behind the administrator's back: the form shows the defaults and says so; only a save replaces the file.
            ModelState.AddModelError(string.Empty, Ui[exception is InvalidDataException ? "admin.transcoding.storedInvalid" : "admin.transcoding.storedUnreadable"]);
            Fill(PlaybackTranscodingSettings.Default);
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Out-of-range input stays out of range after the multiplication instead of overflowing into a valid value.
        long ToBytes(long gibibytes) => Math.Clamp(gibibytes, -1, 1_000_000_000) * PlaybackTranscodingSettings.BytesPerGiB;

        var result = await store.SaveAsync(
            new PlaybackTranscodingSettings(
                TranscodingEnabled,
                SoftwareVideoSessions,
                HardwareVideoSessions,
                RemuxSessions,
                AudioOnlySessions,
                HlsCachePath ?? "",
                ToBytes(CacheBudgetGiB),
                ToBytes(FreeSpaceFloorGiB),
                BufferPreset),
            cancellationToken);
        if (!result.Succeeded)
        {
            foreach (var issue in result.Issues)
            {
                ModelState.AddModelError(FormField(issue.Field), IssueText(issue));
            }

            return Page();
        }

        TempData["Status"] = Ui["admin.transcoding.saved"];
        return RedirectToPage();
    }

    private void Fill(PlaybackTranscodingSettings settings)
    {
        TranscodingEnabled = settings.TranscodingEnabled;
        SoftwareVideoSessions = settings.SoftwareVideoSessions;
        HardwareVideoSessions = settings.HardwareVideoSessions;
        RemuxSessions = settings.RemuxSessions;
        AudioOnlySessions = settings.AudioOnlySessions;
        HlsCachePath = settings.HlsCachePath;
        CacheBudgetGiB = settings.CacheBudgetBytes / PlaybackTranscodingSettings.BytesPerGiB;
        FreeSpaceFloorGiB = settings.FreeSpaceFloorBytes / PlaybackTranscodingSettings.BytesPerGiB;
        BufferPreset = settings.BufferPreset;
    }

    private static string FormField(string settingsField) =>
        settingsField switch
        {
            nameof(PlaybackCostClass.SoftwareVideo) => nameof(SoftwareVideoSessions),
            nameof(PlaybackCostClass.HardwareVideo) => nameof(HardwareVideoSessions),
            nameof(PlaybackCostClass.Remux) => nameof(RemuxSessions),
            nameof(PlaybackCostClass.AudioOnly) => nameof(AudioOnlySessions),
            nameof(PlaybackTranscodingSettings.CacheBudgetBytes) => nameof(CacheBudgetGiB),
            nameof(PlaybackTranscodingSettings.FreeSpaceFloorBytes) => nameof(FreeSpaceFloorGiB),
            nameof(PlaybackTranscodingSettings.BufferPreset) => nameof(BufferPreset),
            _ => nameof(HlsCachePath)
        };

    private string IssueText(PlaybackSettingsIssue issue) =>
        issue.Code switch
        {
            PlaybackSettingsIssueCode.LimitRange => Ui.Format("admin.transcoding.error.limit_range", ("max", PlaybackTranscodingSettings.MaxSessionsPerClass)),
            PlaybackSettingsIssueCode.BudgetRange => Ui.Format("admin.transcoding.error.budget_range", ("min", PlaybackTranscodingSettings.MinCacheBudgetGiB), ("max", PlaybackTranscodingSettings.MaxCacheBudgetGiB)),
            PlaybackSettingsIssueCode.FloorRange => Ui.Format("admin.transcoding.error.floor_range", ("max", PlaybackTranscodingSettings.MaxFreeSpaceFloorGiB)),
            _ => Ui[$"admin.transcoding.error.{JsonNamingPolicy.SnakeCaseLower.ConvertName(issue.Code.ToString())}"]
        };
}
