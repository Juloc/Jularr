using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Library.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Acquisition;
using Jularr.Backend.Data.Logic.Library;
using Jularr.Backend.Logic.Acquisition;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Jularr.Integrations.Data.Subtitles;
using Microsoft.Extensions.Caching.Memory;

namespace Jularr.Backend.Service.Admin.Library.V1
{
    [ServiceEndpoint("GET", "/api/v1/admin/library/files/{storedFileId:long}/subtitles")]
    public sealed class ReadSubtitleFileV1 : AdminService<StoredFileParametersV1>
    {
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<SubtitleFileV1>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library", "Subtitles");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnRead()
        {
            var source = await SubtitleFiles.ReadSourceAsync(Context, new SubtitleSourceParameters(Parameters.StoredFileId), CancellationToken)
                ?? throw new KeyNotFoundException("The video has no subtitle source.");
            return new SubtitleFileV1(source.Id, source.RelativePath, [.. SubtitleFiles.Present(source).Order(StringComparer.Ordinal)], await SubtitleFiles.ReadLanguagesAsync(Context, CancellationToken));
        }
    }

    [ServiceEndpoint("POST", "/api/v1/admin/library/files/{storedFileId:long}/subtitles/search")]
    public sealed class SearchSubtitlesV1 : AdminService<StoredFileParametersV1, SearchSubtitlesDataV1>
    {
        private readonly SubtitleProviders _providers;
        private readonly IMemoryCache _offers;

        internal SearchSubtitlesV1(SubtitleProviders providers, IMemoryCache offers)
        {
            _providers = providers;
            _offers = offers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<BaseDataList<SubtitleOfferV1>>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library", "Subtitles");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            var language = LanguageCodes.ToTwoLetter(Data.Language) ?? throw new DataValidationException(nameof(SearchSubtitlesDataV1.Language), "invalid_type");
            var (source, providers) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await SubtitleFiles.ReadSourceAsync(ctx, new SubtitleSourceParameters(Parameters.StoredFileId), ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Subtitles, ct)),
                CancellationToken);
            if (source is null)
            {
                throw new KeyNotFoundException("The video has no subtitle source.");
            }

            // Every provider's offers in that language, the closest release first; the choice is kept for the download.
            var query = SubtitleFiles.Query(source, [language]);
            var found = new List<(AcquisitionConnectionData Provider, SubtitleOffer Offer)>();
            foreach (var provider in providers)
            {
                try
                {
                    found.AddRange((await _providers.SearchAsync(provider, query, CancellationToken))
                        .Where(offer => LanguageCodes.ToTwoLetter(offer.Language) == language && !offer.IsForeignOnly).Select(offer => (provider, offer)));
                }
                catch (SubtitleException exception)
                {
                    throw new ServiceUnavailableException(exception.Code, exception);
                }
            }

            var ranked = found.OrderByDescending(item => SubtitleFiles.Similarity(item.Offer.ReleaseName, source)).ThenBy(item => item.Offer.IsHearingImpaired)
                .ThenByDescending(item => item.Offer.Downloads).Take(50).ToArray();
            _offers.Set(CacheKey(source.Id), ranked.Select((item, index) => (Id: index.ToString(CultureInfo.InvariantCulture), ProviderId: item.Provider.Id, item.Offer, Language: language))
                .ToDictionary(item => item.Id), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30), Size = Math.Max(1, ranked.Length) });
            return new BaseDataList<SubtitleOfferV1>([.. ranked.Select((item, index) => new SubtitleOfferV1(index.ToString(CultureInfo.InvariantCulture), item.Provider.Name, language,
                item.Offer.ReleaseName, item.Offer.IsHearingImpaired, item.Offer.Downloads))]);
        }

        internal static string CacheKey(long storedFileId) => "subtitles.offers." + storedFileId.ToString(CultureInfo.InvariantCulture);
    }

    [ServiceEndpoint("POST", "/api/v1/admin/library/files/{storedFileId:long}/subtitles")]
    public sealed class DownloadSubtitleV1 : AdminService<StoredFileParametersV1, DownloadSubtitleDataV1>
    {
        private readonly SubtitleFiles _files;
        private readonly SubtitleProviders _providers;
        private readonly IMemoryCache _offers;

        internal DownloadSubtitleV1(SubtitleFiles files, SubtitleProviders providers, IMemoryCache offers)
        {
            _files = files;
            _providers = providers;
            _offers = offers;
        }

        protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<UpdateResult>()];
        protected override ModuleRequirement GetInstanceModules(StoredFileParametersV1 parameters) => ModuleRequirement.All("Library", "Subtitles");
        protected override ServicePermission GetPermission(StoredFileParametersV1 parameters, Type resultType) => new("media.manual.update");

        protected override async Task<IServiceOutput> OnExecute()
        {
            // Only an offer of this file's last search.
            if (!_offers.TryGetValue(SearchSubtitlesV1.CacheKey(Parameters.StoredFileId), out Dictionary<string, (string Id, long ProviderId, SubtitleOffer Offer, string Language)>? offers)
                || !offers!.TryGetValue(Data.OfferId, out var chosen))
            {
                throw new DataValidationException(nameof(DownloadSubtitleDataV1.OfferId), "invalid_transition");
            }

            var (source, providers) = await Context.ExecuteBatchAsync(async (ctx, ct) =>
                (await SubtitleFiles.ReadSourceAsync(ctx, new SubtitleSourceParameters(Parameters.StoredFileId), ct), await ctx.ReadEnabledConnectionsAsync(AcquisitionConnectionRole.Subtitles, ct)),
                CancellationToken);
            var provider = providers.FirstOrDefault(connection => connection.Id == chosen.ProviderId);
            if (source is null || provider is null)
            {
                throw new KeyNotFoundException("The video or its subtitle provider is gone.");
            }

            byte[] bytes;
            try
            {
                bytes = await _providers.DownloadAsync(provider, chosen.Offer, SubtitleFiles.Query(source, [chosen.Language]), CancellationToken);
            }
            catch (SubtitleException exception)
            {
                throw new ServiceUnavailableException(exception.Code, exception);
            }

            if (!SubtitleFiles.IsSubtitle(bytes))
            {
                throw new ServiceUnavailableException("acquisition_invalid_response");
            }

            // A subtitle of that language beside the video stays.
            var directory = _files.Directory(source) ?? throw new ServiceUnavailableException("storage_unavailable");
            return new UpdateResult(await SubtitleFiles.SaveAsync(Context, source, directory, SubtitleFiles.FileName(source, chosen.Language), bytes, CancellationToken));
        }
    }
}
