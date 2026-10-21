using System.Globalization;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Frontend.Framework;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ContinueDetailTests
    {
        private static readonly Dictionary<string, string> Texts = new()
        {
            ["media.episodeCode"] = "S{0} E{1}",
            ["media.episodeOnly"] = "E{0}",
            ["media.timeLeft.minutes"] = "{0} min left",
            ["media.timeLeft.hours"] = "{0} h left",
            ["media.timeLeft.hoursMinutes"] = "{0} h {1} min left",
            ["library.unit.Volume"] = "Volume",
            ["library.unit.Chapter"] = "Chapter",
            ["library.unit.Track"] = "Track"
        };

        private static string? Format(MediaType type, MediaAssetType asset, WorkUnitType? unit, decimal? number, decimal? parent, long position = 0, long? duration = null, decimal? progression = null)
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                var item = new ContinueItemV1(1, 2, type, null, "Title", 2023, null, unit, number, parent, position, duration, progression, asset);
                return ContinueDetail.Format(key => Texts[key], item);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [TestMethod]
        [DataRow(MediaType.Series, MediaAssetType.Video, WorkUnitType.Episode, 5, 1, 1_000_000L, 2_320_000L, null, "S01 E05 · 22 min left")]
        [DataRow(MediaType.Series, MediaAssetType.Video, WorkUnitType.Episode, 105, null, 600_000L, 1_500_000L, null, "E105 · 15 min left")]
        [DataRow(MediaType.Movie, MediaAssetType.Video, null, null, null, 1_200_000L, 7_800_000L, null, "1 h 50 min left")]
        [DataRow(MediaType.Movie, MediaAssetType.Video, null, null, null, 1_800_000L, 5_400_000L, null, "1 h left")]
        [DataRow(MediaType.Music, MediaAssetType.Audio, WorkUnitType.Track, 4, null, 60_000L, 240_000L, null, "Track 4 · 3 min left")]
        [DataRow(MediaType.Movie, MediaAssetType.Video, null, null, null, 5_390_000L, 5_400_000L, null, "1 min left")]
        [DataRow(MediaType.Book, MediaAssetType.Comic, WorkUnitType.Chapter, 183, 20, 0L, null, 0.4, "Chapter 183")]
        [DataRow(MediaType.Book, MediaAssetType.Ebook, WorkUnitType.Volume, 3, null, 0L, null, 0.4, "Volume 3")]
        [DataRow(MediaType.Book, MediaAssetType.Ebook, null, null, null, 0L, null, 0.42, "42%")]
        [DataRow(MediaType.Movie, MediaAssetType.Video, null, null, null, 0L, null, null, null)]
        public void Format_MediaAndPosition_NamesWhereTheProfileIs(MediaType type, MediaAssetType asset, WorkUnitType? unit, int? number, int? parent, long position, long? duration,
            double? progression, string? expected)
        {
            Assert.AreEqual(expected, Format(type, asset, unit, number, parent, position, duration, progression is { } share ? (decimal)share : null));
        }

        [TestMethod]
        public void Format_PositionBeyondTheEnd_ShowsNoTimeLeft()
        {
            Assert.AreEqual("E02", Format(MediaType.Series, MediaAssetType.Video, WorkUnitType.Episode, 2, null, 5_000_000, 4_000_000));
        }
    }
}
