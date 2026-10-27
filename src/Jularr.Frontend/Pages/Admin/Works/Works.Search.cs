using System.Globalization;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Admin.Media.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Operation;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jularr.Frontend.Pages.Admin.Works
{
    public sealed partial class WorksModel
    {
        public Task<IActionResult> OnPostSearchAllAsync(bool all = false)
        {
            _bulkFailed = true;
            return QueueSearchAsync(all ? new QueueWorksSearchDataV1() : new QueueWorksSearchDataV1(null, TypeFilter, MonitoredFilter, StatusFilter, LibraryFilter, ProfileFilter, QueryFilter));
        }

        private async Task<IActionResult> QueueSearchAsync(QueueWorksSearchDataV1 selection)
        {
            var queued = await queueSearch.ExecuteAsync<SearchRunQueuedV1>(new WorksBulkParametersV1(), selection, HttpContext.RequestAborted);
            return Redirect(Address() + (Address().Contains('?') ? "&" : "?") + "searchJob=" + queued.OperationId.ToString(CultureInfo.InvariantCulture));
        }

        public async Task<IActionResult> OnPostCancelSearchAsync([BindRequired] long operationId)
        {
            await cancelSearch.ExecuteAsync<UpdateResult>(new SearchRunParametersV1(operationId), default, HttpContext.RequestAborted);
            return Redirect(Address() + (Address().Contains('?') ? "&" : "?") + "searchJob=" + operationId.ToString(CultureInfo.InvariantCulture));
        }

        public async Task<IActionResult> OnGetSearchProgressAsync()
        {
            _searchRun = (await readSearch.ExecuteAsync<BaseDataList<SearchRunDetailsV1>>(new ReadSearchRunParametersV1(SearchJob), HttpContext.RequestAborted)).FirstOrDefault();
            return Partial("_UiComponent", SearchProgress(antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
        }

        public async Task<IActionResult> OnGetSearchResultsAsync()
        {
            _searchRun = (await readSearch.ExecuteAsync<BaseDataList<SearchRunDetailsV1>>(new ReadSearchRunParametersV1(SearchJob), HttpContext.RequestAborted)).FirstOrDefault();
            if (_searchRun is { } run)
            {
                _searchResults = await readSearchResults.ExecuteAsync<PageResult<WorkBulkOutcomeV1>>(new SearchRunParametersV1(run.OperationId, SearchPage), HttpContext.RequestAborted);
            }

            return Partial("_UiComponent", SearchResultRows());
        }

        private IUiComponent SearchControls(string? csrf)
        {
            var actions = new List<IUiComponent>();
            if (Filtered) actions.Add(new PostForm(Address("SearchAll"), csrf, [], new Button(Glyph("search", 16), new Text("works.search.filtered")).Primary().Submit()));
            actions.Add(new PostForm(Address("SearchAll"), csrf, [("all", "true")], new Button(Glyph("search", 16), new Text("works.search.all")).Submit()));
            actions.Add(new P(new Text("works.search.scope")).Class("works-search-scope"));
            return new Div([.. actions]).Class("works-search-controls");
        }

        private IUiComponent SearchPanel(string? csrf)
        {
            var items = new List<IUiComponent> { SearchProgress(csrf) };
            if (_searchRun is { } run)
            {
                if (_searchResults is { } results)
                {
                    items.Add(new Details(new Summary(new Text("works.search.results")), SearchResultRows().Paged(results.Items.Count, 25, key: "searchPage")));
                }
                else items.Add(new A(new Text("works.search.results")).Class("button button--compact").Attribute("href", SearchAddress(run.OperationId)));
            }

            return new UiSection("works.search.title", items);
        }

        private IUiComponent SearchResultRows()
        {
            if (_searchRun is not { } run || _searchResults is not { } results) return new Div();
            return new Div(new Table<WorkBulkOutcomeV1>(results.Items)
                .Content("media.title", item => new A(new ValueText(item.CanonicalTitle)).Attribute("href", "/Admin/Works/" + item.Id.ToString(CultureInfo.InvariantCulture)))
                .Content("works.col.status", item => new Text(item.OutcomeKey == "pending" && run.IsCancelled ? "works.search.notRun" : OutcomeKey(item.OutcomeKey))))
                .Attribute("data-search-result-rows", "").Attribute("data-refresh", SearchAddress(run.OperationId) + "&handler=SearchResults&searchPage=" + SearchPage.ToString(CultureInfo.InvariantCulture));
        }

        private string SearchAddress(long operationId) => Address() + (Address().Contains('?') ? "&" : "?") + "searchJob=" + operationId.ToString(CultureInfo.InvariantCulture);

        private IUiComponent SearchProgress(string? csrf)
        {
            var box = new Div().Attribute("id", "works-search-progress").Attribute("data-search-progress", "").Attribute("role", "status");
            if (_searchRun is not { } run) return box;
            var running = run.OperationStatusTypeId is OperationStatusType.Queued or OperationStatusType.Running;
            var count = string.Format(CultureInfo.CurrentCulture, texts["works.search.progress"], run.ProcessedCount, run.TargetCount);
            var contents = new List<IUiComponent>
            {
                new StateChip(new Text(run.IsCancelled ? "works.search.cancelled" : "operation.status." + run.OperationStatusTypeId), running ? UiTone.Info : UiTone.Neutral),
                new ValueText(count),
                new Progress().Attribute("max", Math.Max(1, run.TargetCount).ToString(CultureInfo.InvariantCulture)).Attribute("value", run.ProcessedCount.ToString(CultureInfo.InvariantCulture))
                    .TextAttribute("aria-label", "works.search.title"),
                new A(new Text("works.search.refresh")).Class("button button--compact").Attribute("href", SearchAddress(run.OperationId))
            };
            if (running) contents.Add(new PostForm(Address("CancelSearch"), csrf, [("operationId", run.OperationId.ToString(CultureInfo.InvariantCulture))], new Button(new Text("common.cancel")).Submit()));
            return new Div([.. contents]).Attribute("id", "works-search-progress").Attribute("data-search-progress", "").Attribute("role", "status")
                .Attribute("data-running", running ? "true" : "false").Attribute("data-refresh", SearchAddress(run.OperationId) + "&handler=SearchProgress").Class("works-search-progress");
        }
    }
}
