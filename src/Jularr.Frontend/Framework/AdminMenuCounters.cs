using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Request.V1;
using Jularr.Backend.Data.Admin.Worker.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Request.V1;
using Jularr.Backend.Service.Admin.Worker.V1;

namespace Jularr.Frontend.Framework
{
    // The three figures of the administration menu; one the account may not read stays null and its entry shows none.
    public sealed record AdminCounts(int? OpenDownloads, int? FailedJobs, int? OpenRequests)
    {
        public int? Of(AdminCounter counter) => counter switch
        {
            AdminCounter.OpenDownloads => OpenDownloads,
            AdminCounter.FailedJobs => FailedJobs,
            AdminCounter.OpenRequests => OpenRequests,
            _ => null
        };

        // The name the live stream gives the figure (the `menu` entity's texts); a figure of zero has no text, so its badge hides.
        public static string Name(AdminCounter counter) => counter switch
        {
            AdminCounter.OpenDownloads => "downloads",
            AdminCounter.FailedJobs => "jobs",
            _ => "requests"
        };
    }

    // Reads the figures of the menu from the services that already own them, each with the right and module of its own page, for the first render of the sidebar and for the live stream.
    public sealed class AdminMenuCounters(ReadActivitySummaryV1 activity, ReadWorkerJobsV1 jobs, ReadRequestsSummaryV1 requests)
    {
        // A queue summary the caller has just read is reused.
        public async Task<AdminCounts> ReadAsync(UiText texts, CancellationToken cancellationToken, ActivitySummaryV1? summary = null)
        {
            int? downloads = null;
            if (texts.Can("media.read"))
            {
                summary ??= await activity.ExecuteAsync<ActivitySummaryV1>(new ReadActivitySummaryParametersV1(), cancellationToken);
                downloads = summary.ActiveCount + summary.ImportCount;
            }

            int? failed = null;
            if (texts.Can("instance.settings.read"))
            {
                var all = await jobs.ExecuteAsync<BaseDataList<WorkerJobV1>>(new ReadWorkerJobsParametersV1(), cancellationToken);
                failed = all.Count(job => job.WorkerJobStateTypeId == WorkerJobStateType.Failed);
            }

            int? open = null;
            if (texts.Can("requests.read", "Requests"))
            {
                open = (await requests.ExecuteAsync<RequestsSummaryV1>(new ReadRequestsSummaryParametersV1(), cancellationToken)).OpenCount;
            }

            return new AdminCounts(downloads, failed, open);
        }
    }
}
