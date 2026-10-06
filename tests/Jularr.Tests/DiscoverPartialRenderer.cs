using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jularr.Tests;

/// <summary>
/// Renders one compiled Razor partial of Jularr.Web with a model, without a database or a request pipeline,
/// so the markup a browser receives can be asserted on. The partials under test must not need services.
/// </summary>
internal sealed class DiscoverPartialRenderer : IAsyncDisposable
{
    private readonly IHost host;

    private DiscoverPartialRenderer(IHost host) => this.host = host;

    public static async Task<DiscoverPartialRenderer> CreateAsync()
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .UseContentRoot(FindWebProjectRoot())
                .ConfigureServices(services =>
                {
                    services
                        .AddRazorPages()
                        .AddApplicationPart(typeof(Jularr.Web.Pages.IndexModel).Assembly);
                    services.AddLogging();
                    // No page routing here, so the folder of the Discover page is searched for its partials directly.
                    services.Configure<RazorViewEngineOptions>(options =>
                    {
                        options.ViewLocationFormats.Add("/Pages/Discover/{0}.cshtml");
                        options.PageViewLocationFormats.Add("/Pages/Discover/{0}.cshtml");
                    });
                })
                .Configure(app =>
                {
                }))
            .StartAsync();
        return new DiscoverPartialRenderer(host);
    }

    public async Task<string> RenderAsync(string viewPath, object model)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var engine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
        var result = engine.GetView(executingFilePath: null, viewPath, isMainPage: false);
        Assert.IsTrue(result.Success, $"View {viewPath} was not found.");

        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model
        };
        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            result.View,
            viewData,
            new TempDataDictionary(httpContext, scope.ServiceProvider.GetRequiredService<ITempDataProvider>()),
            writer,
            new HtmlHelperOptions());
        await result.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync();
        host.Dispose();
    }

    private static string FindWebProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jularr.sln")))
            {
                return Path.Combine(directory.FullName, "src", "Jularr.Web");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Jularr repository root.");
    }
}
