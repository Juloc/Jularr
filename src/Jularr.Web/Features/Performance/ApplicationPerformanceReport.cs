using Jularr.Web.Features.Admin;

namespace Jularr.Web.Features.Performance;

/// <summary>What Admin shows to answer "what is making Jularr slow or busy": the busiest operations of the last hour, the background budgets and the runtime's own pressure signals.</summary>
public sealed record ApplicationPerformanceReport(
    DateTimeOffset StartedAtUtc,
    IReadOnlyList<PerformanceRow> Routes,
    IReadOnlyList<PerformanceRow> Background,
    IReadOnlyList<PerformanceRow> Providers,
    IReadOnlyList<BackgroundWorkClassState> WorkClasses,
    int ActiveRequests,
    int PeakRequests,
    IReadOnlyDictionary<string, long> RateLimited,
    RuntimeHealthSample? Runtime,
    RuntimeHealthRates? RuntimeRates)
{
    public const int RowsPerCategory = 8;

    public static ApplicationPerformanceReport Build(
        ApplicationPerformanceTelemetry telemetry,
        BackgroundWorkGovernor governor,
        InteractiveLoad load,
        StackResourceSnapshot stack)
    {
        var rows = telemetry.Snapshot();
        IReadOnlyList<PerformanceRow> Top(PerformanceCategory category) => [.. rows.Where(row => row.Category == category).Take(RowsPerCategory)];

        var sampled = stack.History.Where(sample => sample.Runtime is not null).ToArray();
        RuntimeHealthRates? rates = null;
        if (sampled.Length >= 2 && sampled[^1].AtUtc > sampled[^2].AtUtc)
        {
            rates = RuntimeHealthRates.Between(sampled[^2].Runtime!, sampled[^1].Runtime!, sampled[^1].AtUtc - sampled[^2].AtUtc);
        }

        return new ApplicationPerformanceReport(
            telemetry.StartedAtUtc,
            Top(PerformanceCategory.Route),
            Top(PerformanceCategory.Background),
            Top(PerformanceCategory.Provider),
            governor.States(),
            load.Active,
            load.Peak,
            telemetry.RateLimited(),
            sampled.Length == 0 ? RuntimeHealthSample.Capture() : sampled[^1].Runtime,
            rates);
    }
}

/// <summary>The report with the language bundle its partial renders in.</summary>
public sealed record ApplicationPerformanceView(Localization.UiTextBundle Ui, ApplicationPerformanceReport Report);
