using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Service;
using Jularr.Backend.Data.Logic.Media;
using Jularr.Backend.Data.Logic.Profile;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Backend.Logic.Media;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Service.User.Media.V1
{
    // The image of a title that is not a Work, from the cache the Owner sizes; one that is not there is fetched from its provider and kept, never fetched by the browser.
    [ServiceEndpoint("GET", "/api/v1/covers/external")]
    public sealed class ReadExternalCoverV1 : UserService<ExternalCoverParametersV1>
    {
        private readonly CoverStore _covers;
        private readonly IHttpContextAccessor _http;

        internal ReadExternalCoverV1(CoverStore covers, IHttpContextAccessor http)
        {
            _covers = covers;
            _http = http;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkCoverFileV1>()];
        protected override ModuleRequirement GetInstanceModules(ExternalCoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(ExternalCoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            var settings = await Context.ReadCoverCacheSettingsAsync(CancellationToken);
            var acceptsWebP = _http.HttpContext?.Request.Headers.Accept.ToString().Contains("image/webp", StringComparison.OrdinalIgnoreCase) == true;
            var found = await _covers.ReadExternalAsync(Parameters.U, Parameters.W, acceptsWebP, settings.MaximumMegabytes > 0, CancellationToken);
            Stream content = found.Path is { } path ? File.OpenRead(path) : new MemoryStream(found.Bytes!);
            return new WorkCoverFileV1(content, found.ContentType, found.ETag, found.LastModified, false, TimeSpan.FromHours(1));
        }
    }

    // A kept poster, backdrop or episode image of a Work the profile may open; the address names the version, so browsers keep it.
    [ServiceEndpoint("GET", "/api/v1/covers/{workId:long}/{name}")]
    public sealed class ReadWorkCoverV1 : UserService<WorkCoverParametersV1>
    {
        private readonly CoverStore _covers;
        private readonly IHttpContextAccessor _http;

        internal ReadWorkCoverV1(CoverStore covers, IHttpContextAccessor http)
        {
            _covers = covers;
            _http = http;
        }

        protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<WorkCoverFileV1>()];
        protected override ModuleRequirement GetInstanceModules(WorkCoverParametersV1 parameters) => ModuleRequirement.All("Library");
        protected override ServicePermission GetPermission(WorkCoverParametersV1 parameters, Type resultType) => new("profile.media.read");

        protected override async Task<IServiceOutput> OnRead()
        {
            if (!CoverPaths.TryParse(Parameters.Name, out var kind, out var unitId, out var width))
            {
                throw new KeyNotFoundException("The cover was not found.");
            }

            var sql = new SqlStatement();
            sql.Statement =
                """
                SELECT
                    cover."ContentType",
                    cover."Version"
                FROM
                    "WorkCovers" AS cover
                    JOIN "ProfileWorks" AS visible ON visible."WorkId" = cover."WorkId" AND visible."ProfileId" = @ProfileId
                WHERE
                    cover."WorkId" = @WorkId
                    AND cover."CoverKindId" = @CoverKindId
                    AND cover."WorkUnitId" IS NOT DISTINCT FROM @WorkUnitId
                    AND cover."StoredAt" IS NOT NULL;
                """;
            sql.Parameters.Add(new WorkCoverRefData(Parameters.WorkId, kind, unitId));
            sql.Parameters.Add(new ProfileParameters(Auth.ProfileId!.Value));
            var info = await Context.ReadSql.ReadOptionalAsync<WorkCoverFileInfoData>(sql, CancellationToken) ?? throw new KeyNotFoundException("The cover was not found.");
            var acceptsWebP = _http.HttpContext?.Request.Headers.Accept.ToString().Contains("image/webp", StringComparison.OrdinalIgnoreCase) == true;
            var found = _covers.Find(Parameters.WorkId, kind, unitId, width, info.ContentType, acceptsWebP) ?? throw new KeyNotFoundException("The cover was not found.");
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(found.Path), TimeSpan.Zero);
            var tag = "\"" + info.Version + (width?.ToString(CultureInfo.InvariantCulture) ?? "o") + (found.ContentType == "image/webp" ? "w" : "") + "\"";
            return new WorkCoverFileV1(File.OpenRead(found.Path), found.ContentType, tag, written, Parameters.V == info.Version);
        }
    }
}
