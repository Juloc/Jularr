using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Instance;
using Jularr.Backend.Data.System.Instance.V1;
using Jularr.Backend.Logic.Instance;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.System.Instance.V1
{
    // After a restart the last 24 hours of load and service timings come back from the database, once.
    public sealed class LoadPerformanceHistoryV1 : SystemService<PerformanceHistoryParametersV1, NoData>
    {
        private readonly ResourceHistory _history;
        private readonly ServiceTimings _timings;

        internal LoadPerformanceHistoryV1(ResourceHistory history, ServiceTimings timings)
        {
            _history = history;
            _timings = timings;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PerformanceHistoryParametersV1 parameters) => ModuleRequirement.All("Diagnostics");
        protected override ServicePermission GetPermission(PerformanceHistoryParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (samples, timings) = await Context.ExecuteBatchAsync(async (ctx, ct) => (await ctx.ReadPerformanceSamplesAsync(ct), await ctx.ReadServiceTimingsAsync(ct)), CancellationToken);
            _history.Load(samples.Select(sample => new ResourceSample(sample.SampledAt, sample.ProcessorPercent, sample.MemoryBytes)));
            _timings.Load(timings.Select(timing => new ServiceTimingSlice(timing.Service, ServiceTimings.IndexOf(timing.SliceStartedAt), timing.Calls, timing.Errors, timing.TotalMilliseconds,
                timing.MaximumMilliseconds, 0)));
            return new UpdateResult(true);
        }
    }

    // The samples and slices the database does not have yet go in as one batch, and the oldest than 24 hours leave it.
    public sealed class SavePerformanceHistoryV1 : SystemService<PerformanceHistoryParametersV1, NoData>
    {
        private readonly ResourceHistory _history;
        private readonly ServiceTimings _timings;

        internal SavePerformanceHistoryV1(ResourceHistory history, ServiceTimings timings)
        {
            _history = history;
            _timings = timings;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(PerformanceHistoryParametersV1 parameters) => ModuleRequirement.All("Diagnostics");
        protected override ServicePermission GetPermission(PerformanceHistoryParametersV1 parameters, Type resultType) => new("system.operations");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var samples = _history.ReadUnsaved();
            var slices = _timings.ReadUnsaved();
            if (samples.Count == 0 && slices.Count == 0)
            {
                return new UpdateResult(false);
            }

            var data = new PerformanceSaveData([.. samples.Select(sample => new DateTimeOffset(sample.At).ToUnixTimeSeconds())], [.. samples.Select(sample => (long)Math.Round(sample.ProcessorPercent * 10))],
                [.. samples.Select(sample => sample.MemoryBytes)], [.. slices.Select(slice => slice.Service)], [.. slices.Select(slice => new DateTimeOffset(ServiceTimings.StartOf(slice.Index)).ToUnixTimeSeconds())],
                [.. slices.Select(slice => slice.Count)], [.. slices.Select(slice => slice.Errors)], [.. slices.Select(slice => slice.TotalMilliseconds)], [.. slices.Select(slice => slice.MaximumMilliseconds)]);
            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SavePerformanceHistoryAsync(data, ct);
                return true;
            }, CancellationToken);

            // Only what was written counts as saved; anything recorded meanwhile goes in with the next batch.
            if (samples.Count > 0) _history.MarkSaved(samples[^1].At);
            _timings.MarkSaved(slices);
            return new UpdateResult(true);
        }
    }
}
