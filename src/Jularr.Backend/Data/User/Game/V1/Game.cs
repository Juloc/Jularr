using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Playback;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.Fields.Playback;

namespace Jularr.Backend.Data.User.Game.V1
{
    public sealed class GameParametersV1 : BaseParameters
    {
        public GameParametersV1(long mediaAssetId)
        {
            MediaAssetId = mediaAssetId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }
    }

    // The file name ends the address because emulators recognize a game by its extension.
    public sealed class GameFileParametersV1 : BaseParameters
    {
        public GameFileParametersV1(long mediaAssetId, string fileName)
        {
            MediaAssetId = mediaAssetId;
            FileName = fileName;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(GameFields), nameof(GameFields.FileName))]
        public string FileName { get; }
    }

    public sealed class GameSaveParametersV1 : BaseParameters
    {
        public GameSaveParametersV1(long mediaAssetId, GameSaveType gameSaveTypeId)
        {
            MediaAssetId = mediaAssetId;
            GameSaveTypeId = gameSaveTypeId;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(GameFields), nameof(GameFields.GameSaveTypeId))]
        public GameSaveType GameSaveTypeId { get; }
    }

    public sealed class GameV1 : BaseResponse
    {
        public GameV1(long mediaAssetId, long workId, string title, string fileName, string? emulator, long playedMs)
        {
            MediaAssetId = mediaAssetId;
            WorkId = workId;
            Title = title;
            FileName = fileName;
            Emulator = emulator;
            PlayedMs = playedMs;
        }

        [DataField(typeof(MediaAssetFields), nameof(MediaAssetFields.Id))]
        public long MediaAssetId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long WorkId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.CanonicalTitle))]
        public string Title { get; }

        [DataField(typeof(GameFields), nameof(GameFields.FileName))]
        public string FileName { get; }

        // No emulator: the platform is not played in the browser.
        [DataField(typeof(GameFields), nameof(GameFields.Emulator))]
        public string? Emulator { get; }

        [DataField(typeof(GameFields), nameof(GameFields.PlayedMs))]
        public long PlayedMs { get; }
    }

    public sealed class GameFileV1 : BaseResponse, IFileOutput
    {
        public GameFileV1(string filePath)
        {
            FilePath = filePath;
        }

        [JsonIgnore]
        public string FilePath { get; }

        [JsonIgnore]
        public string ContentType => "application/octet-stream";
    }

    public sealed class GameSaveV1 : BaseResponse
    {
        public GameSaveV1(byte[] data, DateTime updatedAt)
        {
            Data = data;
            UpdatedAt = updatedAt;
        }

        [DataField(typeof(GameFields), nameof(GameFields.Data))]
        public byte[] Data { get; }

        [DataField(typeof(GameFields), nameof(GameFields.UpdatedAt))]
        public DateTime UpdatedAt { get; }
    }

    public sealed class UpdateGameSaveDataV1 : BaseData
    {
        public UpdateGameSaveDataV1(byte[] data)
        {
            Data = data;
        }

        [DataField(typeof(GameFields), nameof(GameFields.Data))]
        public byte[] Data { get; }
    }

    public sealed class RecordGamePlayDataV1 : BaseData
    {
        public RecordGamePlayDataV1(int seconds)
        {
            Seconds = seconds;
        }

        [DataField(typeof(GameFields), nameof(GameFields.PlayedSeconds))]
        public int Seconds { get; }
    }
}
