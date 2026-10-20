using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.User.Media.V1
{
    // poster, backdrop or unit-12, optionally with a stored width after a dash; the version only names the file for the browser's cache.
    public sealed class WorkCoverParametersV1 : BaseParameters
    {
        public WorkCoverParametersV1(long workId, string name, string? v = null)
        {
            WorkId = workId;
            Name = name;
            V = v;
        }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.WorkIdRequired))]
        public long WorkId { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.Name))]
        public string Name { get; }

        [DataField(typeof(WorkCoverFields), nameof(WorkCoverFields.Name))]
        public string? V { get; }
    }

    // A provider's image as the cache of titles that are not Works keeps it; the address is the provider's, and only a provider's image host is accepted.
    public sealed class ExternalCoverParametersV1 : BaseParameters
    {
        public ExternalCoverParametersV1(string u, int? w = null)
        {
            U = u;
            W = w;
        }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.ImageUrl))]
        public string U { get; }

        [DataField(typeof(CoverCacheFields), nameof(CoverCacheFields.Width))]
        public int? W { get; }
    }

    public sealed class WorkCoverFileV1 : BaseResponse, ICacheableFileOutput
    {
        public WorkCoverFileV1(Stream content, string contentType, string eTag, DateTimeOffset lastModified, bool isVersioned, TimeSpan? maxAge = null)
        {
            Content = content;
            ContentType = contentType;
            ETag = eTag;
            LastModified = lastModified;
            IsVersioned = isVersioned;
            MaxAge = maxAge;
        }

        [JsonIgnore]
        public TimeSpan? MaxAge { get; }

        [JsonIgnore]
        public Stream Content { get; }

        [JsonIgnore]
        public string ContentType { get; }

        [JsonIgnore]
        public string ETag { get; }

        [JsonIgnore]
        public DateTimeOffset LastModified { get; }

        [JsonIgnore]
        public bool IsVersioned { get; }
    }
}
