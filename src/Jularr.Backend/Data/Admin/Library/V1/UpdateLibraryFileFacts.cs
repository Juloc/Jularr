using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Admin.Library.V1
{
    public sealed class UpdateLibraryFileFactsParametersV1 : BaseParameters
    {
    }

    // A manual override of what a file is, like Sonarr's editor of several files: what stays empty is not changed.
    // The quality must be one of the title's type; the language replaces the language of every audio track of the files, until a changed file is probed again.
    public sealed class UpdateLibraryFileFactsDataV1 : BaseData
    {
        public UpdateLibraryFileFactsDataV1(long[] storedFileIds, string? qualityKey = null, string? audioLanguage = null)
        {
            StoredFileIds = storedFileIds;
            QualityKey = qualityKey;
            AudioLanguage = audioLanguage;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Ids))]
        public long[] StoredFileIds { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.QualityKey), false)]
        public string? QualityKey { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.AudioLanguage), false)]
        public string? AudioLanguage { get; }
    }

    public sealed class UpdateLibraryFileFactsResultV1 : BaseResponse
    {
        public UpdateLibraryFileFactsResultV1(int updatedCount)
        {
            UpdatedCount = updatedCount;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.UpdatedCount))]
        public int UpdatedCount { get; }
    }
}
