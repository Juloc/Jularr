using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Logic.Media
{
    internal sealed class WorkIdentityRowsData : BaseData
    {
        public WorkIdentityRowsData(string rows)
        {
            Rows = rows;
        }

        // JSON rows of Key, MediaTypeId, TmdbId, ImdbId and TvdbId as a provider names its items.
        [DataField(typeof(WorkFields), nameof(WorkFields.MatchRows))]
        public string Rows { get; }
    }

    internal sealed class WorkIdentityMatchData : BaseData
    {
        public WorkIdentityMatchData(string key, long workId)
        {
            Key = key;
            WorkId = workId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.MatchKey))]
        public string Key { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }
    }

    internal sealed class WorkIdentityMatchesData : BaseData
    {
        public WorkIdentityMatchesData(string matches)
        {
            Matches = matches;
        }

        // JSON rows of Key and WorkId.
        [DataField(typeof(WorkFields), nameof(WorkFields.MatchRows))]
        public string Matches { get; }
    }
}
