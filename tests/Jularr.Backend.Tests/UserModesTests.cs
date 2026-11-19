using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Http;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class UserModesTests
    {
        // An address that names a mode or its types switches to it and keeps it; a page's part still loading after a switch reads the mode but keeps the new one.
        [TestMethod]
        public void Resolve_KeepsTheNamedMode_ButAPagePartNeverChangesIt()
        {
            var page = Request("?mode=hear&type=music");
            Assert.AreEqual("hear", UserModes.Resolve(page, 7).Key);
            StringAssert.Contains(page.Response.Headers.SetCookie.ToString(), "jularr-mode-7=hear");

            var part = Request("?type=movies&p=3&handler=Recommendations", "jularr-mode-7=hear");
            Assert.AreEqual("watch", UserModes.Resolve(part, 7).Key, "The part is rendered for the types it names.");
            Assert.AreEqual(0, part.Response.Headers.SetCookie.Count, "The part leaves the chosen mode alone.");

            Assert.AreEqual("hear", UserModes.Resolve(Request("", "jularr-mode-7=hear"), 7).Key);
        }

        private static DefaultHttpContext Request(string query, string? cookie = null)
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString(query.Length > 0 ? query : null);
            if (cookie is not null) context.Request.Headers.Cookie = cookie;
            return context;
        }
    }
}
