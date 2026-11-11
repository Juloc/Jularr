using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;

namespace Jularr.Frontend.Framework
{
    public enum StartupPhase { Checking, BackingUp, Migrating, Starting, Failed }

    // What the start is doing right now, for the status page: the phase, how far the migrations are, how long it has taken, or why it failed.
    public sealed record StartupSnapshot(StartupPhase Phase, bool Updating, int Done, int Total, TimeSpan Elapsed, DatabaseUpgradeFailure? Failure);

    // The state the status page reads while the process starts: written by the start (one thread), read by every request.
    public sealed class StartupStatus
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private StartupPhase _phase = StartupPhase.Checking;
        private bool _updating;
        private int _done;
        private int _total;
        private DatabaseUpgradeFailure? _failure;

        public void Report(DatabaseUpgradeProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);
            lock (_gate)
            {
                if (_phase == StartupPhase.Failed)
                {
                    return;
                }

                _phase = progress.Phase switch
                {
                    DatabaseUpgradePhase.BackingUp => StartupPhase.BackingUp,
                    DatabaseUpgradePhase.Migrating => StartupPhase.Migrating,
                    _ => StartupPhase.Checking
                };
                _updating |= progress.Phase != DatabaseUpgradePhase.Checking;
                _done = progress.Done;
                _total = progress.Total;
            }
        }

        // The database is current; the application is starting.
        public void Starting()
        {
            lock (_gate)
            {
                if (_phase != StartupPhase.Failed)
                {
                    _phase = StartupPhase.Starting;
                }
            }
        }

        public void Fail(DatabaseUpgradeFailure failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            lock (_gate)
            {
                _phase = StartupPhase.Failed;
                _failure = failure;
            }
        }

        public StartupSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new StartupSnapshot(_phase, _updating, _done, _total, _clock.Elapsed, _failure);
            }
        }
    }

    // The status listener gives up the addresses at the last possible moment: when the application's own server (which starts after every hosted
    // service and the middleware pipeline, a few seconds on a real installation) is about to bind them. Without the hook the addresses would be
    // closed from the end of the update until then. Under a test host nothing is armed and the server starts as usual.
    public sealed class StartupHandover
    {
        private Func<Task>? _release;

        public void Arm(Func<Task> release) => _release = release;

        // Wraps the server the host registers (Kestrel) so that its start first releases the status listener.
        public void Install(WebApplicationBuilder builder) => builder.WebHost.ConfigureServices(services =>
        {
            var registration = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IServer));
            if (registration is null)
            {
                return;
            }

            services.Remove(registration);
            services.AddSingleton<IServer>(provider => new HandoverServer(Create(provider, registration), this));
        });

        private Task ReleaseAsync() => Interlocked.Exchange(ref _release, null)?.Invoke() ?? Task.CompletedTask;

        private static IServer Create(IServiceProvider provider, ServiceDescriptor registration) =>
            (IServer)(registration.ImplementationInstance
                ?? registration.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!));

        private sealed class HandoverServer(IServer inner, StartupHandover handover) : IServer
        {
            public IFeatureCollection Features => inner.Features;

            public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
            {
                await handover.ReleaseAsync();
                await inner.StartAsync(application, cancellationToken);
            }

            public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);

            public void Dispose() => inner.Dispose();
        }
    }

    // The listener that answers on the configured addresses from the moment the process starts until the application takes them over: every
    // request gets 503 with the status page (or, after a failed update, the maintenance page), so a reverse proxy never sees a closed port while
    // the backup and the migrations run before the application can serve; the container's probes hear "alive, not ready". It needs neither the
    // database nor the built frontend.
    public sealed class StartupStatusServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private StartupStatusServer(WebApplication app, StartupStatus status)
        {
            _app = app;
            Status = status;
        }

        public StartupStatus Status { get; }

        // Cancelled when the process is told to stop (SIGTERM, Ctrl+C), so a backup or migration in progress is abandoned instead of being
        // followed by the application starting.
        public CancellationToken Stopping => _app.Lifetime.ApplicationStopping;

        public static async Task<StartupStatusServer> StartAsync(string[] args, CancellationToken cancellationToken = default)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
            var app = builder.Build();
            var status = new StartupStatus();
            var assets = new Lazy<FrontendAssets>(() => new FrontendAssets(app.Environment));
            app.Run(async context =>
            {
                if (await Health.AnswerStartingAsync(context))
                {
                    return;
                }

                var snapshot = status.Snapshot();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = snapshot.Failure is null ? "5" : "120";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = "text/html; charset=utf-8";
                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    await context.Response.WriteAsync(snapshot.Failure is { } failure ? DatabaseMaintenance.Page(failure, assets.Value) : StartupStatusPage.Render(snapshot));
                }
            });

            await app.StartAsync(cancellationToken);
            return new StartupStatusServer(app, status);
        }

        // Until the process is stopped (the failed-update case, where the previous version has to be started).
        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => _app.WaitForShutdownAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _app.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    // The page, in German, self-contained (inline styles with the Jularr colours, the mark from the assembly): it shows the step the start is at,
    // how long it has been running, and reloads itself every few seconds until the application answers.
    public static class StartupStatusPage
    {
        public const int ReloadSeconds = 4;

        // The page is UTF-8: German letters stay readable in its source instead of becoming character references.
        private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.BasicLatin, System.Text.Unicode.UnicodeRanges.Latin1Supplement);

        public static string Render(StartupSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var encoder = Encoder;
            var title = DatabaseMaintenance.Text(snapshot.Updating ? "maintenance.startup.title.updating" : "maintenance.startup.title.starting");
            var text = snapshot.Phase switch
            {
                StartupPhase.BackingUp => DatabaseMaintenance.Text("maintenance.startup.text.backup"),
                StartupPhase.Migrating => string.Format(CultureInfo.InvariantCulture, DatabaseMaintenance.Text("maintenance.startup.text.migrating"), snapshot.Done, snapshot.Total),
                StartupPhase.Starting => DatabaseMaintenance.Text("maintenance.startup.text.starting"),
                _ => DatabaseMaintenance.Text("maintenance.startup.text.checking")
            };
            var steps = snapshot.Updating ? Steps(snapshot) : "";
            var elapsed = string.Format(CultureInfo.InvariantCulture, DatabaseMaintenance.Text("maintenance.startup.elapsed"), Elapsed(snapshot.Elapsed));

            return $$"""
                <!DOCTYPE html>
                <html lang="de" dir="ltr">
                <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover" />
                <meta name="color-scheme" content="light dark" />
                <meta http-equiv="refresh" content="{{ReloadSeconds}}" />
                <title>{{encoder.Encode(title)}}</title>
                <style>{{Styles}}</style>
                </head>
                <body>
                <main data-startup-phase="{{snapshot.Phase.ToString().ToLowerInvariant()}}">
                <div class="mark" aria-hidden="true">{{Mark()}}</div>
                <h1>{{encoder.Encode(title)}}</h1>
                <p class="text" role="status">{{encoder.Encode(text)}}</p>
                {{steps}}
                <div class="bar" aria-hidden="true"><span></span></div>
                <p class="note"><span data-startup-elapsed>{{encoder.Encode(elapsed)}}</span> · {{encoder.Encode(DatabaseMaintenance.Text("maintenance.startup.reload"))}}</p>
                </main>
                </body>
                </html>
                """;
        }

        // The three steps of an update with the current one marked: backup, database update, start.
        private static string Steps(StartupSnapshot snapshot)
        {
            var encoder = Encoder;
            var current = snapshot.Phase switch
            {
                StartupPhase.BackingUp => 0,
                StartupPhase.Migrating => 1,
                StartupPhase.Starting => 2,
                _ => -1
            };
            var labels = new[] { "maintenance.startup.step.backup", "maintenance.startup.step.migrate", "maintenance.startup.step.start" };
            var items = new List<string>();
            for (var index = 0; index < labels.Length; index++)
            {
                var state = index < current ? "done" : index == current ? "current" : "next";
                var aria = state == "current" ? " aria-current=\"step\"" : "";
                items.Add($"<li class=\"step step--{state}\"{aria}><span class=\"dot\"></span>{encoder.Encode(DatabaseMaintenance.Text(labels[index]))}</li>");
            }

            return "<ol class=\"steps\">" + string.Concat(items) + "</ol>";
        }

        public static string Elapsed(TimeSpan elapsed)
        {
            var seconds = (long)elapsed.TotalSeconds;
            return seconds < 60
                ? seconds.ToString(CultureInfo.InvariantCulture) + " s"
                : seconds < 3600
                    ? (seconds / 60).ToString(CultureInfo.InvariantCulture) + " Min " + (seconds % 60).ToString(CultureInfo.InvariantCulture) + " s"
                    : (seconds / 3600).ToString(CultureInfo.InvariantCulture) + " Std " + (seconds % 3600 / 60).ToString(CultureInfo.InvariantCulture) + " Min";
        }

        // The brand mark that ships inside the assembly, so the page needs no file or asset.
        private static string Mark()
        {
            using var stream = typeof(StartupStatusPage).Assembly.GetManifestResourceStream("Jularr.Frontend.Pages.Shared.brand-clean.svg");
            if (stream is null)
            {
                return "";
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // The Jularr clean skin: lavender page, white panel, the accent purple; dark when the system is.
        private const string Styles = """
            :root{color-scheme:light dark;font-family:Inter,ui-sans-serif,system-ui,-apple-system,"Segoe UI",Arial,sans-serif;
            --background:light-dark(#faf9ff,#12121d);--surface:light-dark(#fff,#1d1b2b);--border:light-dark(#eae6f4,#38334e);
            --text:light-dark(#26223d,#f4f1ff);--muted:light-dark(#938fa9,#aba5bf);--accent:light-dark(#8040f7,#9a70ff);
            --accent-text:light-dark(#7136d9,#c3a8ff);--accent-soft:light-dark(#f2edff,#35294e);--success:light-dark(#159775,#75dcb6)}
            *{box-sizing:border-box}
            body{margin:0;min-height:100dvh;display:grid;place-items:center;background:var(--background);color:var(--text);
            padding:32px 20px calc(32px + env(safe-area-inset-bottom))}
            main{display:flex;flex-direction:column;align-items:center;width:100%;max-width:520px;text-align:center}
            .mark{width:clamp(96px,24vw,148px);aspect-ratio:1;color:var(--accent);animation:breathe 2.4s ease-in-out infinite}
            .mark svg{display:block;width:100%;height:100%}
            h1{margin:26px 0 10px;font-size:clamp(28px,6vw,44px);font-weight:850;letter-spacing:-.04em;line-height:1.08;text-wrap:balance}
            .text{margin:0;max-width:440px;color:light-dark(#625d7c,#c9c3dc);font-size:16px;line-height:1.55;text-wrap:balance}
            .steps{display:flex;flex-wrap:wrap;justify-content:center;gap:8px;margin:26px 0 0;padding:0;list-style:none}
            .step{display:inline-flex;align-items:center;gap:8px;min-height:36px;padding:0 14px;border:1px solid var(--border);
            border-radius:999px;background:var(--surface);color:var(--muted);font-size:14px;font-weight:600}
            .dot{width:8px;height:8px;border-radius:50%;background:currentColor;opacity:.45}
            .step--done{color:var(--success)}.step--done .dot{opacity:1}
            .step--current{border-color:var(--accent);background:var(--accent-soft);color:var(--accent-text)}
            .step--current .dot{opacity:1;animation:pulse 1.2s ease-in-out infinite}
            .bar{width:min(100%,320px);height:6px;margin-top:28px;overflow:hidden;border-radius:999px;background:var(--accent-soft)}
            .bar span{display:block;width:40%;height:100%;border-radius:inherit;background:var(--accent);animation:slide 1.6s ease-in-out infinite}
            .note{margin:18px 0 0;color:var(--muted);font-size:13px;line-height:1.5}
            @keyframes breathe{50%{transform:scale(1.04);opacity:.85}}
            @keyframes pulse{50%{transform:scale(1.5)}}
            @keyframes slide{0%{transform:translateX(-100%)}100%{transform:translateX(250%)}}
            @media (prefers-reduced-motion:reduce){.mark,.step--current .dot,.bar span{animation:none}.bar span{width:100%;opacity:.5}}
            """;
    }
}
