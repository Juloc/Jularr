using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Data.System.Media.V1;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Library;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Subtitles;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.System.Media.V1
{
    // Fetches the missing subtitles of one video file per call; returns whether there was one.
    public sealed class FetchSubtitlesV1 : SystemService<SubtitleParametersV1, NoData>
    {
        private readonly SubtitleFiles _files;
        private readonly SubtitleProviders _providers;
        private readonly ILogger<FetchSubtitlesV1> _logger;

        internal FetchSubtitlesV1(SubtitleFiles files, SubtitleProviders providers, ILogger<FetchSubtitlesV1> logger)
        {
            _files = files;
            _providers = providers;
            _logger = logger;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(SubtitleParametersV1 parameters) => ModuleRequirement.All("Library", "Subtitles");
        protected override ServicePermission GetPermission(SubtitleParametersV1 parameters, Type resultType) => new("system.media");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var (source, languages, providers) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await SubtitleFiles.ReadSourceAsync(ctx, new SubtitleSourceParameters(null), ct), await SubtitleFiles.ReadLanguagesAsync(ctx, ct),
                    await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Subtitles, ct)), CancellationToken);
            if (source is null)
            {
                return new UpdateResult(false);
            }

            var present = SubtitleFiles.Present(source);
            var missing = languages.Where(language => !present.Contains(language)).ToList();
            if (missing.Count > 0 && providers.Count > 0 && !await FetchAsync(source, missing, providers))
            {
                // Providers that failed or ran out of quota get the file again on the next run.
                return new UpdateResult(false);
            }

            await Context.ExecuteBatchAsync(async (ctx, ct) =>
            {
                await ctx.SaveSubtitlesCheckedAsync(new SubtitleCheckData(source.Id, source.SizeBytes, source.ModifiedAt), ct);
                return true;
            }, CancellationToken);
            return new UpdateResult(true);
        }

        // Whether every provider answered; unreachable storage waits for the next day.
        private async Task<bool> FetchAsync(SubtitleSourceData source, List<string> missing, IReadOnlyList<AcquisitionConnectionData> providers)
        {
            if (_files.Directory(source) is not { } directory)
            {
                return true;
            }

            foreach (var provider in providers)
            {
                if (missing.Count == 0)
                {
                    break;
                }

                try
                {
                    // Best per language: the same release, then hearing aids only when nothing else fits, then the most used.
                    var query = SubtitleFiles.Query(source, missing);
                    var offers = await _providers.SearchAsync(provider, query, CancellationToken);
                    foreach (var language in missing.ToArray())
                    {
                        var best = offers.Where(offer => LanguageCodes.ToTwoLetter(offer.Language) == language && !offer.IsForeignOnly)
                            .OrderByDescending(offer => SubtitleFiles.Similarity(offer.ReleaseName, source))
                            .ThenBy(offer => offer.IsHearingImpaired)
                            .ThenByDescending(offer => offer.Downloads)
                            .FirstOrDefault();
                        if (best is null)
                        {
                            continue;
                        }

                        var bytes = await _providers.DownloadAsync(provider, best, query, CancellationToken);
                        if (!SubtitleFiles.IsSubtitle(bytes))
                        {
                            continue;
                        }

                        try
                        {
                            await SubtitleFiles.SaveAsync(Context, source, directory, SubtitleFiles.FileName(source, language), bytes, CancellationToken);
                        }
                        catch (IOException exception)
                        {
                            _logger.LogWarning(exception, "The subtitle beside stored file {StoredFileId} could not be written.", source.Id);
                            return true;
                        }

                        missing.Remove(language);
                    }
                }
                catch (SubtitleException exception)
                {
                    _logger.LogWarning("Subtitle provider {Provider} failed: {Code}.", provider.Name, exception.Code);
                    return false;
                }
            }

            return true;
        }

    }
}
