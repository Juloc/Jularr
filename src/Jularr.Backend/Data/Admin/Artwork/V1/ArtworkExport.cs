using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Artwork.V1
{
    public sealed class ArtworkExportResultV1 : BaseResponse
    {
        public ArtworkExportResultV1(int savedCount, int skippedCount)
        {
            SavedCount = savedCount;
            SkippedCount = skippedCount;
        }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.Count))]
        public int SavedCount { get; }

        [DataField(typeof(ArtworkFields), nameof(ArtworkFields.Count))]
        public int SkippedCount { get; }
    }
}
