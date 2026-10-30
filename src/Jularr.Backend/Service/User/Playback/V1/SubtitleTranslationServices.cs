using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Playback;
using Jularr.Backend.Data.User.Playback.V1;
using Jularr.Backend.Logic.Playback;
using Jularr.Backend.Logic.Storage;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Logic.Storage;

namespace Jularr.Backend.Service.User.Playback.V1
{
    // A subtitle in the language of the profile, translated by the local model: started here, followed by its status and read as the track "x-language-source".
    [ServiceEndpoint("POST", "/api/v1/playback/assets/{mediaAssetId:long}/subtitle-translations")]
    public sealed class StartSubtitleTranslationV1 : UserService<MediaAssetParametersV1, StartSubtitleTranslationDataV1>
    {
        private readonly StorageAccess _storage;
        private readonly PlaybackSubtitles _subtitles;
        private readonly SubtitleTranslations _translations;

        internal StartSubtitleTranslationV1(StorageAccess storage, PlaybackSubtitles subtitles, SubtitleTranslations translations)
        {
            _storage = storage;
            _subtitles = subtitles;
            _translations = translations;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SubtitleTranslationV1>()];
        protected override ModuleRequirement GetInstanceModules(MediaAssetParametersV1 parameters) => ModuleRequirement.All("Library", "Playback", "Translation");
        protected override ServicePermission GetPermission(MediaAssetParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var language = Data.Language ?? throw new DataValidationException(nameof(Data.Language), "required");
            var sourceLanguage = Data.SourceLanguage ?? "en";
            if (language == sourceLanguage)
            {
                throw new DataValidationException(nameof(Data.Language), "same_language");
            }

            // One short authorized read batch; no SQL transaction stays open while the model runs.
            var (source, streamIndex, mount) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                var (found, stream) = await PlaybackSubtitleSources.FindAsync(sql => ctx.ReadOptionalAsync<PlaybackSubtitleSourceData>(sql, ct), Parameters.MediaAssetId, Auth.ProfileId!.Value, Data.SourceKey);
                return (found, stream, await ctx.ReadStorageMountAsync(found.StorageMountId, ct));
            }, CancellationToken);
            var key = SubtitleTranslationLogic.Key(language, Data.SourceKey);
            var cacheKey = PlaybackSubtitleSources.CacheKey(source, streamIndex, language);
            // A failed translation may be asked for again.
            if (_translations.Status(cacheKey) is { State: not SubtitleTranslations.None and not SubtitleTranslations.Failed } existing)
            {
                return new SubtitleTranslationV1(key, existing.State, existing.Done, existing.Total);
            }

            var (server, model) = await _translations.ModelAsync(await Context.ExecuteBatchAsync(LocalTranslationMachines.SavedAsync, CancellationToken), CancellationToken);
            var files = _storage.Open(mount);
            var path = StoragePath.Combine(source.RootRelativePath, source.RelativePath);
            var sourceCacheKey = source.StoredFileId.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + "-" + (streamIndex is { } stream ? PlaybackTracks.StreamKey(stream) : "file") + "-"
                + source.ModifiedAt.Ticks.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            var vtt = await _subtitles.ReadAsync(files, path, streamIndex, sourceCacheKey, false, CancellationToken);
            var started = _translations.Start(cacheKey, vtt, sourceLanguage, language, server, model);
            return new SubtitleTranslationV1(key, started.State, started.Done, started.Total);
        }
    }

    [ServiceEndpoint("GET", "/api/v1/playback/assets/{mediaAssetId:long}/subtitle-translations/{key}")]
    public sealed class ReadSubtitleTranslationV1 : UserService<PlaybackSubtitleParametersV1>
    {
        private readonly SubtitleTranslations _translations;

        internal ReadSubtitleTranslationV1(SubtitleTranslations translations)
        {
            _translations = translations;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SubtitleTranslationV1>()];
        protected override ModuleRequirement GetInstanceModules(PlaybackSubtitleParametersV1 parameters) => ModuleRequirement.All("Library", "Playback", "Translation");
        protected override ServicePermission GetPermission(PlaybackSubtitleParametersV1 parameters, Type resultType) => new("profile.playback");

        protected override async Task<IServiceOutput> OnRead()
        {
            if (!SubtitleTranslationLogic.TryParseKey(Parameters.Key, out var language, out var sourceKey))
            {
                throw new KeyNotFoundException("The translation was not found.");
            }

            var (source, streamIndex) = await PlaybackSubtitleSources.FindAsync(sql => Context.ReadSql.ReadOptionalAsync<PlaybackSubtitleSourceData>(sql, CancellationToken), Parameters.MediaAssetId, Auth.ProfileId!.Value, sourceKey);
            var status = _translations.Status(PlaybackSubtitleSources.CacheKey(source, streamIndex, language));
            return new SubtitleTranslationV1(Parameters.Key, status.State, status.Done, status.Total);
        }
    }
}
