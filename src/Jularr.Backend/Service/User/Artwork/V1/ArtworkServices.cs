using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Artwork.V1;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;

namespace Jularr.Backend.Service.User.Artwork.V1
{
    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/units/{workUnitId:long}/artwork/{slotId}.jxl")]
    public sealed class ReadUnitArtworkJxlV1 : UserService<ArtworkUnitReadParametersV1>
    {
        private readonly ArtworkCache _cache;

        internal ReadUnitArtworkJxlV1(ArtworkCache cache) => _cache = cache;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ArtworkJxlImageV1>()];
        protected override ModuleRequirement GetInstanceModules(ArtworkUnitReadParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ArtworkUnitReadParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var query = new ArtworkReadParametersV1(Parameters.WorkId, Parameters.SlotId, Parameters.WorkUnitId);
            var preview = await Context.ReadArtworkPreviewAsync(query, new ProfileParameters(Auth.ProfileId!.Value), CancellationToken);
            var bytes = _cache.Read(preview?.JxlCacheKey, Jularr.Backend.Data.Enums.Media.ArtworkFormat.Jxl);
            return bytes is { Length: > 0 } ? new ArtworkJxlImageV1(bytes) : throw new KeyNotFoundException("JPEG XL unit artwork is not cached.");
        }
    }

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/units/{workUnitId:long}/artwork/{slotId}")]
    public sealed class ReadUnitArtworkV1 : UserService<ArtworkUnitReadParametersV1>
    {
        private readonly ArtworkCache _cache;

        internal ReadUnitArtworkV1(ArtworkCache cache) => _cache = cache;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ArtworkImageV1>()];
        protected override ModuleRequirement GetInstanceModules(ArtworkUnitReadParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ArtworkUnitReadParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var query = new ArtworkReadParametersV1(Parameters.WorkId, Parameters.SlotId, Parameters.WorkUnitId);
            var preview = await Context.ReadArtworkPreviewAsync(query, new ProfileParameters(Auth.ProfileId!.Value), CancellationToken);
            var bytes = _cache.Read(preview?.CacheKey) ?? preview?.FallbackBytes;
            return bytes is { Length: > 0 } ? new ArtworkImageV1(bytes) : throw new KeyNotFoundException("Unit artwork is not cached.");
        }
    }

    // Exact MIME type allows <picture> to pick the native JPEG XL source only where supported.
    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/artwork/{slotId}.jxl")]
    public sealed class ReadArtworkJxlV1 : UserService<ArtworkReadParametersV1>
    {
        private readonly ArtworkCache _cache;

        internal ReadArtworkJxlV1(ArtworkCache cache) => _cache = cache;
        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ArtworkJxlImageV1>()];
        protected override ModuleRequirement GetInstanceModules(ArtworkReadParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ArtworkReadParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var preview = await Context.ReadArtworkPreviewAsync(Parameters, new ProfileParameters(Auth.ProfileId!.Value), CancellationToken);
            if (preview is null) throw new KeyNotFoundException("Artwork is not available.");
            var data = _cache.Read(preview.JxlCacheKey, Jularr.Backend.Data.Enums.Media.ArtworkFormat.Jxl);
            return data is { Length: > 0 } ? new ArtworkJxlImageV1(data) : throw new KeyNotFoundException("JPEG XL artwork is not cached.");
        }
    }

    [ServiceEndpoint("GET", "/api/v1/works/{workId:long}/artwork/{slotId}")]
    public sealed class ReadArtworkV1 : UserService<ArtworkReadParametersV1>
    {
        private readonly ArtworkCache _cache;

        internal ReadArtworkV1(ArtworkCache cache)
        {
            _cache = cache;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<ArtworkImageV1>()];
        protected override ModuleRequirement GetInstanceModules(ArtworkReadParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ArtworkReadParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var preview = await Context.ReadArtworkPreviewAsync(Parameters, new ProfileParameters(Auth.ProfileId!.Value), CancellationToken);
            if (preview is null)
            {
                throw new KeyNotFoundException("Artwork is not available.");
            }

            var bytes = _cache.Read(preview.CacheKey) ?? preview.FallbackBytes;
            return bytes is { Length: > 0 } ? new ArtworkImageV1(bytes) : throw new KeyNotFoundException("Artwork is not available.");
        }
    }
}
