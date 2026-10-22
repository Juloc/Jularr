using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Tests
{
    // The one paging model: a page is up to PageSize rows, nothing is counted or looked ahead, Next is offered iff the page is full.
    [TestClass]
    public sealed class PagingTests
    {
        [TestMethod]
        [DataRow(1, 3, 25, false, false)]
        [DataRow(1, 25, 25, true, false)]
        [DataRow(2, 25, 25, true, true)]
        [DataRow(2, 7, 25, false, true)]
        [DataRow(3, 0, 25, false, true)]
        public void Rule_NextIffThePageIsFull_PreviousFromPageTwo(int page, int rows, int pageSize, bool next, bool previous)
        {
            Assert.AreEqual(next, DockPaging.HasNext(rows, pageSize));
            Assert.AreEqual(previous, DockPaging.HasPrevious(page));

            var paging = DockPaging.Of(page, rows, pageSize, number => "/list?pageNumber=" + number);
            if (!next && !previous)
            {
                Assert.IsNull(paging, "A short first page needs no pager.");
                return;
            }

            Assert.IsNotNull(paging);
            Assert.AreEqual(next ? "/list?pageNumber=" + (page + 1) : null, paging.NextHref);
            Assert.AreEqual(previous ? "/list?pageNumber=" + (page - 1) : null, paging.PreviousHref);
        }

        [TestMethod]
        public void Link_SetsOnlyItsOwnKey_AndKeepsTheOtherListsAndTheFilters()
        {
            var query = Query("?q=a%20b&files=2&Notes=4&handler=Bulk");
            Assert.AreEqual("/x?q=a%20b&files=2&Notes=4&pageNumber=3", PagingScope.Link("/x", query, "pageNumber", 3));
            Assert.AreEqual("/x?q=a%20b&Notes=4&files=3", PagingScope.Link("/x", query, "files", 3));
            Assert.AreEqual("/x?q=a%20b&Notes=4", PagingScope.Link("/x", query, "files", 1), "Page 1 leaves the key out; the other list keeps its page.");
            Assert.AreEqual("/x?q=a%20b&files=2&notes=5", PagingScope.Link("/x", query, "notes", 5), "Keys are matched without case.");
            Assert.AreEqual(2, PagingScope.PageOf(query, "FILES"));
            Assert.AreEqual(1, PagingScope.PageOf(query, "pageNumber"));
            Assert.AreEqual(1, PagingScope.PageOf(Query("?pageNumber=0"), "pageNumber"));
            Assert.AreEqual(1, PagingScope.PageOf(Query("?pageNumber=x"), "pageNumber"));
        }

        [TestMethod]
        public void TwoPagedLists_PageIndependently_AndTheFirstOneIsInTheDock()
        {
            var (scope, page) = Build("?files=2&notes=4", () =>
            {
                _ = new Span().Paged(25, 25, "files");
                _ = new Span().Paged(3, 25, "notes");
            });

            var files = scope.Lists[0];
            var notes = scope.Lists[1];
            Assert.AreEqual(2, files.Page);
            Assert.AreEqual(4, notes.Page);
            Assert.AreEqual("/admin?notes=4", files.Paging!.PreviousHref, "The files list steps back and keeps the notes page.");
            Assert.AreEqual("/admin?notes=4&files=3", files.Paging.NextHref);
            Assert.AreEqual("/admin?files=2&notes=3", notes.Paging!.PreviousHref, "The notes list steps back and keeps the files page.");
            Assert.IsNull(notes.Paging.NextHref, "A short page has no next.");

            Assert.IsTrue(files.IsDocked);
            Assert.IsFalse(notes.IsDocked, "Every other list draws its own pager under itself.");
            Assert.AreEqual(1, page.Content.OfType<ActionDock>().Count());
        }

        [TestMethod]
        public void ThePrimaryListIsTheOneMarkedPrimary()
        {
            var (scope, page) = Build("", () =>
            {
                _ = new Span().Paged(25, 25, "first");
                _ = new Span().Paged(25, 25, "second", primary: true);
            });

            Assert.IsFalse(scope.Lists[0].IsDocked);
            Assert.IsTrue(scope.Lists[1].IsDocked);
            Assert.AreEqual(1, page.Content.OfType<ActionDock>().Count());
        }

        [TestMethod]
        public void APageWithItsOwnDock_GetsThePagerThere_AndAShortFirstPageAddsNoDock()
        {
            var (_, withDock) = Build("", () => _ = new Span().Paged(25), page => page.Content.Add(new ActionDock(new FrontendAssets(null!), "x")));
            Assert.AreEqual(1, withDock.Content.OfType<ActionDock>().Count(), "The list's pager joins the dock the page has.");

            var (_, none) = Build("", () => _ = new Span().Paged(10));
            Assert.AreEqual(0, none.Content.OfType<ActionDock>().Count());
        }

        private static (PagingScope Scope, Page Page) Build(string query, Action define, Action<Page>? beforeDefine = null)
        {
            var request = new DefaultHttpContext().Request;
            request.Path = "/admin";
            request.QueryString = new QueryString(query);
            var page = new Page();
            beforeDefine?.Invoke(page);
            var scope = PagingScope.Begin(request, new FrontendAssets(null!));
            try
            {
                define();
            }
            finally
            {
                PagingScope.End();
            }

            scope.Attach(page);
            return (scope, page);
        }

        private static IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> Query(string query) =>
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);
    }
}
