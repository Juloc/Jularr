using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Admin.Acquisition.V1
{
    // Empty entries defer to the configured default category.
    public sealed class DownloadClientCategoriesV1 : BaseData
    {
        public DownloadClientCategoriesV1(string? movie, string? tv, string? anime, string? book, string? audiobook, string? manga, string? lightNovel,
            string? music, string? game)
        {
            Movie = movie;
            Tv = tv;
            Anime = anime;
            Book = book;
            Audiobook = audiobook;
            Manga = manga;
            LightNovel = lightNovel;
            Music = music;
            Game = game;
        }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? Movie { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? Tv { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? Anime { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? Book { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? Audiobook { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? Manga { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? LightNovel { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? Music { get; }

        [DataField(typeof(AcquisitionConnectionFields), nameof(AcquisitionConnectionFields.DownloadCategory))]
        public string? Game { get; }
    }
}
