using System.Globalization;
using System.Text.Encodings.Web;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Localization;

namespace Jularr.Frontend.Framework
{
    // What Jularr serves when the database update at its start failed: no application, no database, one page on the configured addresses that
    // answers every request with 503 and names the migration. The update was rolled back, so the previous version can be started again. The
    // process stays up (rather than exiting) so a reverse proxy shows this page instead of a bad gateway and a restart policy does not
    // repeat the backup and the failing update in a loop; fixing it means starting the previous or a corrected version.
    public static class DatabaseMaintenance
    {
        public static async Task RunAsync(string[] args, DatabaseUpgradeFailure failure, CancellationToken cancellationToken = default)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            var page = Page(failure, new FrontendAssets(app.Environment));
            app.UseStaticFiles();
            app.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "120";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(page);
            });

            await app.RunAsync(cancellationToken);
        }

        // The page: the title and one line with the migration name, in German; the log holds the reason. It styles itself with the compiled
        // error-state styles when they are built and stays readable without them.
        public static string Page(DatabaseUpgradeFailure failure, FrontendAssets? assets = null)
        {
            var encoder = HtmlEncoder.Default;
            var title = Text("maintenance.databaseUpdate.title");
            var text = string.Format(CultureInfo.InvariantCulture, Text("maintenance.databaseUpdate.text"), failure.Migration);
            string styles;
            try
            {
                using var writer = new StringWriter(CultureInfo.InvariantCulture);
                assets?.Styles("Assets/Shared/shell.entry.scss", "Pages/Shared/ErrorState.entry.ts").WriteTo(writer, encoder);
                styles = writer.ToString();
            }
            catch (InvalidOperationException)
            {
                // The frontend was not built (tests, a bare checkout): the page is plain.
                styles = "";
            }

            return $"""
                <!DOCTYPE html>
                <html lang="de" dir="ltr" data-skin="clean" data-mode="system">
                <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover" />
                <meta name="color-scheme" content="light dark" />
                <title>{encoder.Encode(title)}</title>
                {styles}
                </head>
                <body class="auth-page">
                <main class="auth-main" id="main-content">
                <section class="error-state error-state--500 error-state--standalone" data-error-code="503">
                <h1 class="error-state__title">{encoder.Encode(title)}</h1>
                <p class="error-state__text" role="alert">{encoder.Encode(text)}</p>
                </section>
                </main>
                </body>
                </html>
                """;
        }

        // German as the catalog ships it, English when the catalog lacks the text.
        private static string Text(string key) =>
            UiCatalogs.Load().FirstOrDefault(catalog => catalog.Locale == "de")?.Texts.GetValueOrDefault(key).Text is { Length: > 0 } german
                ? german
                : UiResources.English.First(resource => resource.Key == key).EnglishText;
    }
}
