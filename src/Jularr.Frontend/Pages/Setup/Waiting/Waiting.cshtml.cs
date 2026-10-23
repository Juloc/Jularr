using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Setup.Waiting
{
    // What everyone but the Owner sees while the instance is being set up.
    public sealed class WaitingModel : PreparedPage
    {
        protected override void Define(Page page)
        {
            page.Title("setup.waiting.title");
            page.Content.Add(new Div(new P(new Text("setup.waiting")), new A(new Text("account.logout.submit")).Class("button").Attribute("href", "/Account/Logout")).Class("setup-waiting"));
        }
    }
}
