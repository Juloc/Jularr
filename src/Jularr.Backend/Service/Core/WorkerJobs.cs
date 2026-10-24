using System.Threading.Channels;
using Jularr.Backend.Data.Enums.Worker;
using Jularr.Backend.Data.Logic.Worker;
using Jularr.Backend.Data.System.Worker.V1;

namespace Jularr.Backend.Service.Core
{
    // A background job: what it is called, which worker runs it and when it is due. Every periodic or hosted piece of work is declared here once.
    public sealed class WorkerJobDefinition
    {
        internal WorkerJobDefinition(string key, WorkerGroupType group, TimeSpan? interval, TimeSpan startDelay, bool canRunNow = true, bool canPauseWhileUsed = false, bool pausesWhileUsedByDefault = false,
            WorkerIntervalSettingType intervalSetting = WorkerIntervalSettingType.None, string? parentKey = null)
        {
            IntervalSetting = intervalSetting;
            ParentKey = parentKey;
            Key = key;
            Group = group;
            Interval = interval;
            StartDelay = startDelay;
            CanRunNow = canRunNow;
            CanPauseWhileUsed = canPauseWhileUsed;
            PausesWhileUsedByDefault = pausesWhileUsedByDefault;
        }

        public string Key { get; }
        public WorkerGroupType Group { get; }

        // Between two passes; none for a job that runs back to back while there is work.
        public TimeSpan? Interval { get; }

        // A small fixed offset after the start, so the workers do not all begin at once.
        public TimeSpan StartDelay { get; }
        public bool CanRunNow { get; }

        // The Owner setting that replaces the interval, when there is one.
        public WorkerIntervalSettingType IntervalSetting { get; }

        // A part of another job (one provider of the provider sync): it shares that job's schedule and has its own switch and result.
        public string? ParentKey { get; }
        public bool CanPauseWhileUsed { get; }
        public bool PausesWhileUsedByDefault { get; }
    }

    public enum WorkerJobGate
    {
        Run,
        NotDue,
        Paused,
        WaitingForUse
    }

    // The one owner of what background jobs do and when: their schedule, the Owner's pause and run-now, and how the last pass went.
    // The process holds the newest state; the database keeps it across a restart, written at most once a minute unless the outcome changes.
    // Jobs run in this process alone: nothing claims their work across several instances.
    public sealed class WorkerJobs
    {
        private static readonly TimeSpan PersistEvery = TimeSpan.FromMinutes(1);

        // A signed-in person counts as using Jularr as long as the provider catalogs say so.
        private static readonly TimeSpan UseLasts = Logic.Media.ExternalWorkLogic.UseLasts;

        public static IReadOnlyList<WorkerJobDefinition> Definitions { get; } =
        [
            // The scan, backup and benchmark queues run beside each other, each when work arrives.
            new("operations", WorkerGroupType.Operation, null, TimeSpan.Zero, canRunNow: false),
            new("backupQueue", WorkerGroupType.Operation, null, TimeSpan.Zero, canRunNow: false),
            new("benchmarkQueue", WorkerGroupType.Operation, null, TimeSpan.Zero, canRunNow: false),

            // What a restart interrupts, the queue and the running downloads, is picked up at once; the rest follows one by one.
            // Tracking looks every minute and every 15 seconds while a download runs.
            new("downloads", WorkerGroupType.Download, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            new("wantedSearch", WorkerGroupType.Acquisition, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10), intervalSetting: WorkerIntervalSettingType.WantedSearchMinutes),

            // One read of the connected media servers; each provider has its own switch and result.
            new("providerSync", WorkerGroupType.Provider, TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(40), canPauseWhileUsed: true, pausesWhileUsedByDefault: true,
                intervalSetting: WorkerIntervalSettingType.ProviderSyncMinutes),
            new("providerSyncPlex", WorkerGroupType.Provider, null, TimeSpan.Zero, canRunNow: false, parentKey: "providerSync"),
            new("providerSyncJellyfin", WorkerGroupType.Provider, null, TimeSpan.Zero, canRunNow: false, parentKey: "providerSync"),
            new("providerSyncEmby", WorkerGroupType.Provider, null, TimeSpan.Zero, canRunNow: false, parentKey: "providerSync"),
            new("usenet", WorkerGroupType.Usenet, null, TimeSpan.FromSeconds(6), canRunNow: false),
            new("aniListMatching", WorkerGroupType.Media, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), canPauseWhileUsed: true, pausesWhileUsedByDefault: true),
            // The relations of the matched anime, manga and light novels; skipped while the AniList module is off.
            new("aniListRelations", WorkerGroupType.Media, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(32), canPauseWhileUsed: true, pausesWhileUsedByDefault: true),
            // Every profile's own AniList list and progress; waits while Jularr is used, as the other syncs do.
            new("aniListSync", WorkerGroupType.Media, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(45), canPauseWhileUsed: true),
            new("covers", WorkerGroupType.Media, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(35), canPauseWhileUsed: true),
            // Brings stored covers into the Owner's format, a few at a time, and only while nobody uses Jularr.
            new("coverBackfill", WorkerGroupType.Media, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(55), canPauseWhileUsed: true, pausesWhileUsedByDefault: true),
            new("storage", WorkerGroupType.Storage, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5)),
            new("scanScheduling", WorkerGroupType.Storage, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15), canPauseWhileUsed: true, pausesWhileUsedByDefault: true),
            // Looks every half hour whether a backup is due; the Owner's interval says how old the newest may get.
            new("backup", WorkerGroupType.Backup, TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(90), intervalSetting: WorkerIntervalSettingType.BackupHours),
            new("maintenance", WorkerGroupType.Maintenance, TimeSpan.FromHours(1), TimeSpan.FromSeconds(120)),
            new("trickplay", WorkerGroupType.Trickplay, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(75), canPauseWhileUsed: true, pausesWhileUsedByDefault: true),
            new("subtitles", WorkerGroupType.Subtitle, TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(100), canPauseWhileUsed: true),
            new("recognition", WorkerGroupType.Recognition, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20), canPauseWhileUsed: true, pausesWhileUsedByDefault: true),
            new("readingCopies", WorkerGroupType.Derivation, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(25), canPauseWhileUsed: true, pausesWhileUsedByDefault: true),

            // Each catalog decides for itself whether it waits while Jularr is used.
            new("discoverFeeds", WorkerGroupType.Catalog, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(8)),
            new("catalog", WorkerGroupType.Catalog, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(8)),
            new("resources", WorkerGroupType.Sampler, TimeSpan.FromMinutes(1), TimeSpan.Zero, canRunNow: false),

            // Asks every configured service whether it answers; a service that does not makes the pass fail, which tells the Owner in Activity.
            new("health", WorkerGroupType.Health, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(12))
        ];

        public static WorkerJobDefinition Definition(string key) =>
            Definitions.FirstOrDefault(definition => definition.Key == key) ?? throw new KeyNotFoundException("The job is not known.");

        private readonly TimeProvider _time;
        private readonly UserActivity _activity;
        private readonly object _gate = new();
        private readonly Dictionary<string, JobState> _states;
        private readonly Dictionary<WorkerGroupType, Channel<bool>> _requests;
        private WorkerSettingsData _settings = new(24, 7, 30, 2);

        public WorkerJobs(TimeProvider time, UserActivity activity)
        {
            _time = time;
            _activity = activity;
            var startedAt = time.GetUtcNow().UtcDateTime;
            _states = Definitions.ToDictionary(definition => definition.Key, definition => new JobState { NextRunAt = startedAt + definition.StartDelay });
            _requests = Enum.GetValues<WorkerGroupType>().ToDictionary(group => group, _ => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite
            }));
        }

        // The jobs of one worker, and how often it has to look.
        public static IReadOnlyList<WorkerJobDefinition> Of(WorkerGroupType group) => [.. Definitions.Where(definition => definition.Group == group)];

        // False until the Owner's choices are read from the database; workers wait for it so a paused job never runs once after a restart.
        public bool IsLoaded
        {
            get
            {
                lock (_gate)
                {
                    return _loaded;
                }
            }
        }

        private bool _loaded;

        internal void Load(IEnumerable<WorkerJobRowData> rows, WorkerSettingsData? settings = null)
        {
            lock (_gate)
            {
                if (settings is not null)
                {
                    UseSettings(settings);
                }

                foreach (var row in rows)
                {
                    if (!_states.TryGetValue(row.Key, out var state)) continue;

                    // The Owner's choices come from the database; the process keeps what it ran itself since the start.
                    state.IsPaused = row.IsPaused;
                    state.PausesWhileUsedChoice = row.PausesWhileUsedChoice;
                    state.PersistedResult = row.LastResultTypeId;
                    state.PersistedErrorKey = row.LastErrorKey;
                    state.PersistedAt = _time.GetUtcNow().UtcDateTime;
                    if (state.LastFinishedAt is null)
                    {
                        state.LastStartedAt = row.LastStartedAt;
                        state.LastFinishedAt = row.LastFinishedAt;
                        state.LastResult = row.LastResultTypeId;
                        state.LastErrorKey = row.LastErrorKey;
                    }
                }

                _loaded = true;
            }
        }

        // Whether the job may run now; it is then marked as running until Finish.
        public WorkerJobGate Begin(string key)
        {
            var definition = Definition(key);
            lock (_gate)
            {
                var state = _states[key];
                if (state.IsPaused) return WorkerJobGate.Paused;

                var now = _time.GetUtcNow().UtcDateTime;
                if (!state.RunNowRequested && now < state.NextRunAt) return WorkerJobGate.NotDue;

                // Someone asked for it: it runs even while Jularr is used.
                if (!state.RunNowRequested && PausesWhileUsed(definition, state) && _activity.IsUsedWithin(UseLasts))
                {
                    state.IsWaitingForUse = true;
                    return WorkerJobGate.WaitingForUse;
                }

                state.IsRequestedRun = state.RunNowRequested;
                state.RunNowRequested = false;
                state.IsWaitingForUse = false;
                state.IsRunning = true;
                state.LastStartedAt = now;
                return WorkerJobGate.Run;
            }
        }

        // Whether the pass that is running was asked for by the Owner.
        public bool IsRequestedRun(string key)
        {
            lock (_gate)
            {
                return _states[key].IsRequestedRun;
            }
        }

        // A long pass checks between its steps whether it should go on.
        public bool IsHalted(string key)
        {
            var definition = Definition(key);
            lock (_gate)
            {
                var state = _states[key];
                return state.IsPaused || PausesWhileUsed(definition, state) && _activity.IsUsedWithin(UseLasts);
            }
        }

        // The pass is over. What has to be kept is returned; a quiet success is only kept once a minute.
        public RecordWorkerJobDataV1? Finish(string key, WorkerJobResultType result, string? errorKey)
        {
            var definition = Definition(key);
            lock (_gate)
            {
                var state = _states[key];
                var now = _time.GetUtcNow().UtcDateTime;
                state.IsRunning = false;
                state.LastFinishedAt = now;
                state.LastResult = result;
                state.LastErrorKey = errorKey;
                state.NextRunAt = now + (Cadence(definition) ?? TimeSpan.Zero);
                var isChange = state.PersistedResult != result || state.PersistedErrorKey != errorKey || state.PersistedAt is null || now - state.PersistedAt >= PersistEvery;
                if (!isChange) return null;

                state.PersistedResult = result;
                state.PersistedErrorKey = errorKey;
                state.PersistedAt = now;
                return new RecordWorkerJobDataV1(state.LastStartedAt, now, result, errorKey, Cadence(definition) is null ? null : state.NextRunAt);
            }
        }

        // A job that has to look again sooner than its interval, such as tracking while downloads run.
        public void RunAgainWithin(string key, TimeSpan delay)
        {
            lock (_gate)
            {
                var state = _states[key];
                if (state.LastFinishedAt is { } finished && finished + delay < state.NextRunAt)
                {
                    state.NextRunAt = finished + delay;
                }
            }
        }

        // The intervals the Owner chose: a job already waiting for its next pass counts from its last one.
        internal void UseSettings(WorkerSettingsData settings)
        {
            lock (_gate)
            {
                _settings = settings;
                foreach (var definition in Definitions.Where(definition => definition.IntervalSetting != WorkerIntervalSettingType.None && Cadence(definition) is not null))
                {
                    var state = _states[definition.Key];
                    if (state.LastFinishedAt is { } finished)
                    {
                        state.NextRunAt = finished + Cadence(definition)!.Value;
                    }
                }
            }
        }

        // The interval the Owner sees: their setting, else the fixed one; zero is off.
        public TimeSpan? Interval(string key) => IntervalOf(Definition(key));

        private TimeSpan? IntervalOf(WorkerJobDefinition definition)
        {
            lock (_gate)
            {
                return definition.IntervalSetting switch
                {
                    WorkerIntervalSettingType.BackupHours => TimeSpan.FromHours(_settings.BackupIntervalHours),
                    WorkerIntervalSettingType.ProviderSyncMinutes => TimeSpan.FromMinutes(_settings.ProviderSyncIntervalMinutes),
                    WorkerIntervalSettingType.WantedSearchMinutes => TimeSpan.FromMinutes(_settings.WantedSearchIntervalMinutes),
                    _ => definition.Interval
                };
            }
        }

        // How soon a pass looks again; the backup job keeps looking every half hour whatever its interval is.
        private TimeSpan? Cadence(WorkerJobDefinition definition) => definition.IntervalSetting == WorkerIntervalSettingType.BackupHours ? definition.Interval : IntervalOf(definition);

        // Run now: the job's next pass starts at once and its worker is woken. A paused job or one that runs on its own is not.
        public bool RequestRun(string key)
        {
            var definition = Definition(key);
            lock (_gate)
            {
                if (!definition.CanRunNow || _states[key].IsPaused) return false;
                _states[key].RunNowRequested = true;
            }

            _requests[definition.Group].Writer.TryWrite(true);
            return true;
        }

        // A worker sleeping until its next tick also wakes for a run now.
        public async ValueTask<bool> WaitForRunRequestAsync(WorkerGroupType group, CancellationToken ct)
        {
            var reader = _requests[group].Reader;
            var any = await reader.WaitToReadAsync(ct);
            reader.TryRead(out _);
            return any;
        }

        internal void Pause(string key, bool isPaused)
        {
            lock (_gate)
            {
                _states[key].IsPaused = isPaused;
                if (isPaused) _states[key].IsWaitingForUse = false;
            }
        }

        internal void SetPausesWhileUsed(string key, bool pausesWhileUsed)
        {
            lock (_gate)
            {
                _states[key].PausesWhileUsedChoice = pausesWhileUsed;
            }
        }

        // The job as it is now, from the process and what the database kept of it.
        internal WorkerJobStatus Describe(string key, WorkerJobRowData? row)
        {
            var definition = Definition(key);
            lock (_gate)
            {
                var state = _states[key];
                var isPaused = _loaded ? state.IsPaused : row?.IsPaused ?? false;
                var pausesWhileUsed = _loaded ? PausesWhileUsed(definition, state) : definition.CanPauseWhileUsed && (row?.PausesWhileUsedChoice ?? definition.PausesWhileUsedByDefault);
                var hasRun = state.LastFinishedAt is not null;
                var result = hasRun ? state.LastResult : row?.LastResultTypeId;
                var error = hasRun ? state.LastErrorKey : row?.LastErrorKey;
                var stateType = state.IsRunning ? WorkerJobStateType.Running
                    : isPaused ? WorkerJobStateType.Paused
                    : state.IsWaitingForUse ? WorkerJobStateType.WaitingForUse
                    : result switch
                    {
                        WorkerJobResultType.Failed => WorkerJobStateType.Failed,
                        WorkerJobResultType.Skipped => WorkerJobStateType.Off,
                        WorkerJobResultType.Succeeded => WorkerJobStateType.Idle,
                        _ => WorkerJobStateType.Pending
                    };
                return new WorkerJobStatus(stateType, isPaused, pausesWhileUsed, IntervalOf(definition), hasRun ? state.LastStartedAt : row?.LastStartedAt, hasRun ? state.LastFinishedAt : row?.LastFinishedAt, error,
                    isPaused || Cadence(definition) is null ? null : state.NextRunAt);
            }
        }

        private static bool PausesWhileUsed(WorkerJobDefinition definition, JobState state) =>
            definition.CanPauseWhileUsed && (state.PausesWhileUsedChoice ?? definition.PausesWhileUsedByDefault);

        private sealed class JobState
        {
            public bool IsPaused { get; set; }
            public bool? PausesWhileUsedChoice { get; set; }
            public bool IsRunning { get; set; }
            public bool IsWaitingForUse { get; set; }
            public bool RunNowRequested { get; set; }
            public bool IsRequestedRun { get; set; }
            public DateTime? LastStartedAt { get; set; }
            public DateTime? LastFinishedAt { get; set; }
            public WorkerJobResultType? LastResult { get; set; }
            public string? LastErrorKey { get; set; }
            public DateTime NextRunAt { get; set; }
            public WorkerJobResultType? PersistedResult { get; set; }
            public string? PersistedErrorKey { get; set; }
            public DateTime? PersistedAt { get; set; }
        }
    }

    internal sealed class WorkerJobStatus(WorkerJobStateType state, bool isPaused, bool pausesWhileUsed, TimeSpan? interval, DateTime? lastStartedAt, DateTime? lastFinishedAt, string? lastErrorKey, DateTime? nextRunAt)
    {
        public WorkerJobStateType State { get; } = state;
        public bool IsPaused { get; } = isPaused;
        public bool PausesWhileUsed { get; } = pausesWhileUsed;
        public TimeSpan? Interval { get; } = interval;
        public DateTime? LastStartedAt { get; } = lastStartedAt;
        public DateTime? LastFinishedAt { get; } = lastFinishedAt;
        public string? LastErrorKey { get; } = lastErrorKey;
        public DateTime? NextRunAt { get; } = nextRunAt;
    }
}
