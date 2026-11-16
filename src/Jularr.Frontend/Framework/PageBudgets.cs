using System.Diagnostics;
using System.Globalization;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;

namespace Jularr.Frontend.Framework
{
    // The server time a warm page may take (owner's decision: 30 ms for every page) and its measurement. Each page request records its whole time under its view
    // path (a named handler or a POST after it, never the address) in the service timings, which follow the Diagnostics module; the Performance page compares them
    // with the budget. An administrator's response names the shell (UiText) and data (the page handler) parts in Server-Timing, after the page's own entries;
    // the rendering is the rest, which the header cannot carry because it is sent before the rendering ends.
    public static class PageBudgets
    {
        public static readonly TimeSpan Server = TimeSpan.FromMilliseconds(30);

        public static IApplicationBuilder UseJularrPageTimings(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            // Only pages; an error page shown again for a page's status counts with that page.
            if (context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is not { } page || context.Features.Get<PageTiming>() is not null)
            {
                await next(context);
                return;
            }

            var timings = context.RequestServices.GetRequiredService<ServiceTimings>();
            var timing = new PageTiming();
            context.Features.Set(timing);
            context.Response.OnStarting(() =>
            {
                if (timings.IsEnabled && context.RequestServices.GetRequiredService<UiText>().Can("instance.settings.read", "Diagnostics"))
                {
                    var parts = string.Create(CultureInfo.InvariantCulture, $"shell;dur={timing.Shell.TotalMilliseconds:0.#}, data;dur={timing.Data.TotalMilliseconds:0.#}");
                    lock (timing.Steps) if (timing.Steps.Count > 0) parts += ", " + string.Join(", ", timing.Steps);
                    context.Response.Headers.Append("Server-Timing", parts);
                }

                return Task.CompletedTask;
            });

            // Only server faults count as errors, as for services.
            var started = Stopwatch.GetTimestamp();
            var failed = false;
            try
            {
                await next(context);
                failed = context.Response.StatusCode >= StatusCodes.Status500InternalServerError;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed = true;
                throw;
            }
            finally
            {
                timings.RecordPage(page.ViewEnginePath + timing.Handler, Stopwatch.GetElapsedTime(started), failed);
            }
        });

        // UiText's preparation of the shell.
        internal static void Shell(HttpContext context, TimeSpan elapsed)
        {
            if (context.Features.Get<PageTiming>() is { } timing) timing.Shell += elapsed;
        }

        // The page handler; a handler other than the plain GET keeps its own key (":GetRecommendations", ":Post", ":PostSave").
        internal static void Data(HttpContext context, TimeSpan elapsed, HandlerMethodDescriptor? handler)
        {
            if (context.Features.Get<PageTiming>() is not { } timing) return;
            timing.Data += elapsed;
            if (handler is not null && (handler.Name is not null || handler.HttpMethod != "Get")) timing.Handler = ":" + handler.HttpMethod + handler.Name;
        }

        // A step of the page handler ("hero", "recent"), named after the shell and data parts; reads that run together report from any thread.
        internal static void Step(HttpContext context, string name, TimeSpan elapsed)
        {
            if (context.Features.Get<PageTiming>() is not { } timing) return;
            var entry = string.Create(CultureInfo.InvariantCulture, $"{name};dur={elapsed.TotalMilliseconds:0.#}");
            lock (timing.Steps) timing.Steps.Add(entry);
        }

        private sealed class PageTiming
        {
            public readonly List<string> Steps = [];
            public TimeSpan Shell;
            public TimeSpan Data;
            public string Handler = "";
        }
    }
}
