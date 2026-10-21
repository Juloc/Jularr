using System.Globalization;
using Jularr.Backend.Data.Admin.Transcoding.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Service.Admin.Transcoding.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Transcoding
{
    // What this machine converts video with, a benchmark of it, and the limits playback keeps.
    public sealed class TranscodingModel(ReadTranscodeSettingsV1 readSettings, UpdateTranscodeSettingsV1 updateSettings, ReadTranscodeBenchmarkV1 readBenchmark, QueueTranscodeBenchmarkV1 queue,
        IAntiforgery antiforgery, UiText texts) : JularrPage
    {
        private const string Home = "/Admin/Transcoding";
        private static readonly int[] LadderChoices = [2160, 1440, 1080, 720, 480, 360];

        private TranscodeSettingsV1? _settings;
        private TranscodeBenchmarkV1? _benchmark;
        private UpdateTranscodeSettingsDataV1? _input;
        private bool _benchmarkFailed;

        protected override void Define(Page page)
        {
            page.Title("admin.transcoding.title");
            page.Navigation("nav.admin");
            page.Asset("Pages/Admin/Transcoding/Transcoding.entry.ts");
            var csrf = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
            if (_benchmark is not { } benchmark || _settings is not { } settings)
            {
                if (FormError is not null) page.Content.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
                return;
            }

            // The encoders of this machine with the speeds the last benchmark measured.
            page.Content.Add(new UiSection("admin.transcoding.hardware", [new Table<TranscodeHardwareV1>(benchmark.Hardware)
                .Content("admin.transcoding.acceleration", item => new Text("admin.transcoding.acceleration." + item.TranscodeAccelerationTypeId))
                .Text(item => item.Encoder, "admin.transcoding.encoder")
                .Content("admin.transcoding.device", item => new ValueText(item.Device ?? "–"))
                .Content("admin.transcoding.available", item => new Text(item.IsAvailable ? "common.yes" : "common.no"))
                .Content("admin.transcoding.speed1080", item => Speed(benchmark, item.TranscodeAccelerationTypeId, 1080))
                .Content("admin.transcoding.speed4k", item => Speed(benchmark, item.TranscodeAccelerationTypeId, 2160))]));

            // The last run with what it proposes; running or failed it says so.
            var facts = new List<(string, IUiComponent)>
            {
                ("admin.transcoding.ranAt", benchmark.RanAt is { } ranAt ? new ValueText(ranAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : new Text("admin.transcoding.never"))
            };
            if (benchmark.RanAt is not null)
            {
                facts.Add(("admin.transcoding.cores", new ValueText((benchmark.CpuCores ?? 0).ToString(CultureInfo.CurrentCulture))));
                facts.Add(("admin.transcoding.proposedAcceleration", new Text("admin.transcoding.acceleration." + benchmark.BestTranscodeAccelerationTypeId)));
                facts.Add(("admin.transcoding.proposedMaximum", new ValueText((benchmark.RecommendedMaxTranscodes ?? 0).ToString(CultureInfo.CurrentCulture))));
            }

            var section = new List<IUiComponent> { new FactList(facts) };
            if (benchmark.IsRunning) section.Add(new P(new Text("admin.transcoding.running")).Attribute("role", "status").Attribute("data-transcoding-running", ""));
            else if (benchmark.ErrorKey is { } error) section.Add(new P(new Text("admin.transcoding.failed"), new ValueText(" "), new Text("errors." + error)).Attribute("role", "alert"));
            if (_benchmarkFailed && FormError is not null) section.Add(new P(new Text(FormError.ResourceKey)).Attribute("role", "alert"));
            if (!benchmark.IsRunning) section.Add(new Form<TranscodeBenchmarkParametersV1>(form => form.Submit("admin.transcoding.run"), Home + "?handler=Benchmark", csrf, new TranscodeBenchmarkParametersV1()));
            page.Content.Add(new UiSection("admin.transcoding.benchmark", [.. section]));

            // The choices playback follows; the page's save pill saves them.
            var accelerations = Enum.GetValues<TranscodeAcceleration>().Select(item => new SelectOption(((byte)item).ToString(CultureInfo.InvariantCulture), texts["admin.transcoding.acceleration." + item]));
            var ladder = LadderChoices.Select(height => new SelectOption(height.ToString(CultureInfo.InvariantCulture), height.ToString(CultureInfo.CurrentCulture) + "p"));
            page.Content.Add(new UiSection("admin.transcoding.settings", [new Form<UpdateTranscodeSettingsDataV1>(form =>
            {
                form.Hidden(item => item.Version);
                form.Select(item => item.TranscodeAccelerationTypeId, "admin.transcoding.acceleration", accelerations);
                form.Number(item => item.MaxTranscodes, "admin.transcoding.maxTranscodes");
                form.Choices(item => item.LadderHeights, "admin.transcoding.ladder", ladder);
                form.Save();
            }, Home, csrf, _input ?? new UpdateTranscodeSettingsDataV1(settings.TranscodeAccelerationTypeId, settings.MaxTranscodes, settings.LadderHeights, settings.Version),
                _benchmarkFailed ? null : FormError)]));
        }

        // Seconds of picture a second, or a note where the pipeline did not run.
        private static IUiComponent Speed(TranscodeBenchmarkV1 benchmark, TranscodeAcceleration acceleration, int height) =>
            benchmark.Results.FirstOrDefault(item => item.TranscodeAccelerationTypeId == acceleration && item.Height == height)?.SpeedFactor is { } speed
                ? new ValueText(speed.ToString("0.0", CultureInfo.CurrentCulture) + " ×")
                : new Text("admin.transcoding.notRun");

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        protected override async Task LoadDataAsync()
        {
            var token = HttpContext.RequestAborted;
            _settings = await readSettings.ExecuteAsync<TranscodeSettingsV1>(new TranscodeSettingsParametersV1(), token);
            _benchmark = await readBenchmark.ExecuteAsync<TranscodeBenchmarkV1>(new TranscodeBenchmarkParametersV1(), token);
        }

        public async Task<IActionResult> OnPostAsync([BindRequired] TranscodeAcceleration transcodeAccelerationTypeId, int maxTranscodes, int[]? ladderHeights, long version)
        {
            // An empty ladder offers no lower picture.
            _input = new UpdateTranscodeSettingsDataV1(transcodeAccelerationTypeId, maxTranscodes, ladderHeights ?? [], version);
            await updateSettings.ExecuteAsync<UpdateResult>(new TranscodeSettingsParametersV1(), _input, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }

        public async Task<IActionResult> OnPostBenchmarkAsync()
        {
            _benchmarkFailed = true;
            await queue.ExecuteAsync<UpdateResult>(new TranscodeBenchmarkParametersV1(), default, HttpContext.RequestAborted);
            return LocalRedirect(Home);
        }
    }
}
