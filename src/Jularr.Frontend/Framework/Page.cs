using System.Globalization;
using System.Text.Json;
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

        // The quiet hint of an action lives in TempData until the response that shows it; the layout shows it for a page without script.
        public const string HintKey = "JularrHint";
        private const int HintLength = 200;

        // The result of an action as the short hint at the bottom of the page (a resource key, with the values it formats), never as content of the page.
        protected void Hint(string key, params object[] values)
        {
            var texts = HttpContext.RequestServices.GetRequiredService<UiText>();
            HintText(values.Length == 0 ? texts[key] : string.Format(CultureInfo.CurrentCulture, texts[key], values));
        }

        protected void HintText(string text) => TempData[HintKey] = text.Length > HintLength ? text[..HintLength] : text;

        // An action that leaves for a page without the shell (a deleted profile) says nothing.
        protected void NoHint() => TempData[HintKey] = "";

        // Account and Setup pages leave the shell, which shows the hint.
        private bool HintsOnSuccess => !Request.Path.StartsWithSegments("/Account") && !Request.Path.StartsWithSegments("/Setup");

        // Shared service failure feedback.
        public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            // A POST that shows its page again adds no history entry; after a redirect the target address is recorded.
            if (HttpMethods.IsPost(Request.Method)) Response.Headers["HX-Push-Url"] = "false";

            // Reject failed HTTP binding.
            if (!context.ModelState.IsValid)
            {
                context.Result = await PrepareFailureAsync(ServiceError.FromException(new BadHttpRequestException("Request values could not be bound.")));
                return;
            }

            var executed = await next();
            if (executed.Exception is null || executed.ExceptionHandled || executed.Exception is OperationCanceledException)
            {
                if (executed.Exception is null) AnswerHint(executed.Result);
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

        // An action that went through says so: a handler's own hint, else "Saved" for every successful POST that leads on to a page. A script's request
        // gets the hint with the page it swaps (HX-Trigger); a page without script shows it itself (ActionToast).
        private void AnswerHint(IActionResult? result)
        {
            if (result is null) return;
            var leads = result is RedirectResult or LocalRedirectResult or RedirectToPageResult or RedirectToRouteResult or RedirectToActionResult;
            if (leads && HttpMethods.IsPost(Request.Method) && HintsOnSuccess && TempData.Peek(HintKey) is null) Hint("actionToast.saved");
            if (result is not PageResult || !Request.Headers.ContainsKey("HX-Request") || TempData[HintKey] is not string { Length: > 0 } text) return;
            Response.Headers["HX-Trigger"] = JsonSerializer.Serialize(new Dictionary<string, object> { ["jularr:hint"] = new { text } });
            TempData.Remove(HintKey);
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

            // A top page of an administration entry opens with the entry's row of sibling pages (a page that merges it with its own filters, or is a detail, says so).
            if (page.Shell && !page.SuppressPageRow && page.Parents.Count == 0 && Request.Path.StartsWithSegments("/Admin")
                && AdminMenu.Siblings(HttpContext.RequestServices.GetRequiredService<UiText>(), Request) is { } row)
            {
                page.Content.Insert(0, new ListToolbar(row));
            }

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

    // One breadcrumb part: a resource key or an item's own name, linking to the page it names.
    public sealed record Crumb(string Text, string Href, bool IsKey = true);

    public sealed class Page
    {
        public bool Shell { get; set; } = true;
        public bool IsPlayer { get; set; }
        public bool IsReader { get; set; }
        public string? TitleKey { get; private set; }
        public string? DocumentTitle { get; private set; }
        public string? NavigationKey { get; private set; }
        public List<Crumb> Parents { get; } = [];
        public List<IUiComponent> Content { get; } = [];
        public List<string> AssetEntries { get; } = [];

        // Set by a page that draws the row of its administration entry itself (merged with its filters) or has none (a detail page).
        public bool SuppressPageRow { get; private set; }
        public void NoPageRow() => SuppressPageRow = true;

        // The breadcrumb names the page; a document title names the shown item in the browser tab.
        public void Title(string key, string? documentTitle = null)
        {
            TitleKey = key;
            DocumentTitle = documentTitle;
        }
        // A page below another names it in the breadcrumb; the document title then names the page itself.
        // The shell adds the area (Admin) and the sidebar group by itself; a page names only what lies between.
        public void Parent(string key, string href) => Parents.Add(new Crumb(key, href));

        // The same for a named item (a title, a collection) instead of a resource key.
        public void ParentName(string name, string href) => Parents.Add(new Crumb(name, href, false));

        public void Asset(string entry)
        {
            if (!AssetEntries.Contains(entry, StringComparer.Ordinal)) AssetEntries.Add(entry);
        }
        public void Navigation(string key) => NavigationKey = key;

        public void Use(Page definition)
        {
            Shell = definition.Shell;
            SuppressPageRow = definition.SuppressPageRow;
            IsPlayer = definition.IsPlayer;
            IsReader = definition.IsReader;
            TitleKey = definition.TitleKey;
            DocumentTitle = definition.DocumentTitle;
            NavigationKey = definition.NavigationKey;
            Parents.AddRange(definition.Parents);
            Content.AddRange(definition.Content);
            foreach (var asset in definition.AssetEntries) Asset(asset);
        }
    }
}
