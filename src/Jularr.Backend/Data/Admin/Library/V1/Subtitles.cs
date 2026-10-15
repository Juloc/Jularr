using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;

namespace Jularr.Backend.Data.Admin.Library.V1
{
    // A video file with the subtitle languages it has and the ones the instance wants.
    public sealed class SubtitleFileV1 : BaseResponse
    {
        public SubtitleFileV1(long storedFileId, string relativePath, string[] presentLanguages, string[] wantedLanguages)
        {
            StoredFileId = storedFileId;
            RelativePath = relativePath;
            PresentLanguages = presentLanguages;
            WantedLanguages = wantedLanguages;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Id))]
        public long StoredFileId { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.RelativePath))]
        public string RelativePath { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SubtitleLanguages))]
        public string[] PresentLanguages { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SubtitleLanguages))]
        public string[] WantedLanguages { get; }
    }

    public sealed class SearchSubtitlesDataV1 : BaseData
    {
        public SearchSubtitlesDataV1(string language)
        {
            Language = language;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SubtitleLanguage))]
        public string Language { get; }
    }

    // One provider's subtitle; the id holds for the following minutes only.
    public sealed class SubtitleOfferV1 : BaseResponse
    {
        public SubtitleOfferV1(string offerId, string providerName, string language, string? releaseName, bool isHearingImpaired, long downloads)
        {
            OfferId = offerId;
            ProviderName = providerName;
            Language = language;
            ReleaseName = releaseName;
            IsHearingImpaired = isHearingImpaired;
            Downloads = downloads;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SubtitleOfferId))]
        public string OfferId { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.Name))]
        public string ProviderName { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SubtitleLanguage))]
        public string Language { get; }

        [DataField(typeof(AcquisitionReleaseFields), nameof(AcquisitionReleaseFields.Title), false)]
        public string? ReleaseName { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.IsHearingImpaired))]
        public bool IsHearingImpaired { get; }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.Downloads))]
        public long Downloads { get; }
    }

    public sealed class DownloadSubtitleDataV1 : BaseData
    {
        public DownloadSubtitleDataV1(string offerId)
        {
            OfferId = offerId;
        }

        [DataField(typeof(StoredFileFields), nameof(StoredFileFields.SubtitleOfferId))]
        public string OfferId { get; }
    }
}
