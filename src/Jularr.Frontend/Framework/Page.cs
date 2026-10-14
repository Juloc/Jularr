using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Filters;
using Jularr.Backend.Service.Core;

namespace Jularr.Frontend.Framework
{
    public abstract class JularrPage : PageModel
    {
        public Page Definition { get; private set; } = new();
        private ServiceError? _error;
        protected ServiceError? FormError => _error is not null && HttpMethods.IsPost(Request.Method) ? _error : null;

        // Shared service failure feedback.
        public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            // Reject failed HTTP binding.
            if (!context.ModelState.IsValid)
            {
                context.Result = await PrepareFailureAsync(ServiceError.FromException(new BadHttpRequestException("Request values could not be bound.")));
                return;
            }

            var executed = await next();
            if (executed.Exception is null || executed.ExceptionHandled || executed.Exception is OperationCanceledException)
            {
                return;
            }

            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<JularrPage>>();
            var error = ServiceError.Describe(executed.Exception, HttpContext, logger);

            // Otherwise the shared handler answers.
            try
            {
                executed.Result = await PrepareFailureAsync(error);
                executed.ExceptionHandled = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The error page of {Path} failed.", Request.Path);
            }
        }

        private async Task<IActionResult> PrepareFailureAsync(ServiceError error)
        {
            _error = error;
            Response.StatusCode = error.StatusCode;

            // Reload only failed mutations.
            if (HttpMethods.IsPost(Request.Method))
            {
                try
                {
                    await LoadDataAsync();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _error = ServiceError.Describe(exception, HttpContext, HttpContext.RequestServices.GetRequiredService<ILogger<JularrPage>>());
                    Response.StatusCode = _error.StatusCode;
                }
            }

            PreparePage();
            return Page();
        }

        protected abstract void Define(Page page);

        protected virtual Task LoadDataAsync() => Task.CompletedTask;

        protected void PreparePage() => Definition = BuildDefinition();

        internal Page BuildDefinition()
        {
            var page = new Page();
            Define(page);
            if (_error is not null && HttpMethods.IsGet(Request.Method))
            {
                // Failed reads always explain why.
                page.Content.Insert(0, new P(ErrorText.Of(_error)).Class("ui-page-error").Attribute("role", "alert"));
            }
            return page;
        }
    }

    public abstract class PreparedPage : JularrPage
    {
        public IActionResult OnGet()
        {
            PreparePage();
            return Page();
        }
    }

    public sealed class Page
    {
        public bool Shell { get; set; } = true;
        public bool IsPlayer { get; set; }
        public string? TitleKey { get; private set; }
        public string? NavigationKey { get; private set; }
        public List<IUiComponent> Content { get; } = [];
        public List<string> AssetEntries { get; } = [];

        public void Title(string key) => TitleKey = key;
        public void Asset(string entry)
        {
            if (!AssetEntries.Contains(entry, StringComparer.Ordinal)) AssetEntries.Add(entry);
        }
        public void Navigation(string key) => NavigationKey = key;

        public void Use(Page definition)
        {
            Shell = definition.Shell;
            IsPlayer = definition.IsPlayer;
            TitleKey = definition.TitleKey;
            NavigationKey = definition.NavigationKey;
            Content.AddRange(definition.Content);
            foreach (var asset in definition.AssetEntries) Asset(asset);
        }
    }
}
