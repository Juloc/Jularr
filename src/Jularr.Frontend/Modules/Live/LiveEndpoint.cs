using System.Globalization;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jularr.Backend.Data.Admin.Activity.V1;
using Jularr.Backend.Data.Admin.Live.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.User.Live.V1;
using Jularr.Backend.Service.Admin.Activity.V1;
using Jularr.Backend.Service.Admin.Live.V1;
using Jularr.Backend.Service.Core;
using Jularr.Backend.Service.User.Live.V1;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Http.Features;

namespace Jularr.Frontend.Modules.Live
{
    // The one live stream of the host: Server-Sent Events whose data comes from the live services with the reader's own rights.
    // A snapshot comes first, so a new or reconnected stream never needs a reload; after that an event follows only when the entities differ.
    public static class LiveEndpoint
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        public static IEndpointRouteBuilder MapJularrLive(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/live/events", StreamAsync).RequireAuthorization();
            return endpoints;
        }

        private static async Task StreamAsync(HttpContext http, LiveChanges changes, UiText texts, ReadLiveV1 own, ReadLiveAdminV1 admin, ReadActivitySummaryV1 queue, IHostApplicationLifetime lifetime)
        {
            // A stopping host ends its streams instead of waiting for them.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
            var cancellationToken = linked.Token;

            // A person, or a browser, holds only a few streams.
            using var lease = changes.TryJoin(LiveChanges.OwnerOf(http.User));
            if (lease is null)
            {
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers.RetryAfter = "30";
                return;
            }

            await texts.InitializeAsync(http);
            var reads = new Reads(texts.Can("profile.requests.read", "Requests"), texts.Can("media.read"));
            if (!reads.Own && !reads.Admin)
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var culture = CultureInfo.GetCultureInfo(texts.Locale);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            // Nothing along the way may hold the answers back.
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await http.Response.StartAsync(cancellationToken);
            await WriteAsync(http, "retry: 5000\n\n", cancellationToken);

            string? last = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    // The version is taken before reading, so a change during the read is not lost.
                    var seen = changes.Sequence;
                    var payload = await ReadAsync(http, texts, reads, own, admin, queue, cancellationToken);
                    if (payload != last)
                    {
                        await WriteAsync(http, "event: " + (last is null ? "snapshot" : "update") + "\ndata: " + payload + "\n\n", cancellationToken);
                        last = payload;
                    }

                    // Until something changes; a quiet stream says it is alive.
                    while (await changes.WaitForChangeAsync(seen, changes.HeartbeatInterval, cancellationToken) <= seen)
                    {
                        await WriteAsync(http, ": heartbeat\n\n", cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The page went away.
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
            {
                // The session ended or the right was withdrawn: the stream ends, the next one finds out why.
            }
        }

        private static async Task<string> ReadAsync(HttpContext http, UiText texts, Reads reads, ReadLiveV1 own, ReadLiveAdminV1 admin, ReadActivitySummaryV1 queue, CancellationToken cancellationToken)
        {
            var entities = new Dictionary<string, LiveEntity>(StringComparer.Ordinal);

            // The profile's own requests, newest first: the newest one of a Work speaks for its button.
            if (reads.Own)
            {
                LiveChanges.RequireFreshSession(http);
                foreach (var request in await own.ExecuteAsync<BaseDataList<LiveRequestV1>>(new ReadLiveParametersV1(), cancellationToken))
                {
                    var entity = LiveEntities.Request(texts, request);
                    entities[LiveKeys.Request(request.Id)] = entity;
                    entities.TryAdd(LiveKeys.RequestOfWork(request.WorkId), entity);
                }
            }

            // An administrator also sees every request, the queue and the targets.
            if (reads.Admin)
            {
                LiveChanges.RequireFreshSession(http);
                var all = await admin.ExecuteAsync<LiveAdminV1>(new ReadLiveAdminParametersV1(), cancellationToken);
                foreach (var request in all.Requests) entities[LiveKeys.AdminRequest(request.Id)] = LiveEntities.AdminRequest(texts, request);
                foreach (var operation in all.Operations) entities[LiveKeys.Operation(operation.Id)] = LiveEntities.Operation(texts, operation);
                foreach (var wanted in all.Wanted) entities[LiveKeys.Wanted(wanted.Id)] = LiveEntities.Wanted(texts, wanted);
                entities[LiveKeys.Queue] = LiveEntities.Queue(await queue.ExecuteAsync<ActivitySummaryV1>(new ReadActivitySummaryParametersV1(), cancellationToken));
            }

            return JsonSerializer.Serialize(new { entities }, Json);
        }

        private static async Task WriteAsync(HttpContext http, string text, CancellationToken cancellationToken)
        {
            await http.Response.WriteAsync(text, cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
        }

        private sealed record Reads(bool Own, bool Admin);
    }
}
