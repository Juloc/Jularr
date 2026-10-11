using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages;

public sealed class PlayerModel : JularrPage
{
    protected override void Define(Page page)
    {
        page.Title("player.title");
        page.Asset("Modules/Player/player.entry.scss");
        page.Shell = false;
        page.IsPlayer = true;
        // Media-first player only. PlaybackPlan/ActiveSession and real media source are backend contracts.
        page.Content.Add(new UiPlayer());
    }

    public IActionResult OnGet(long id)
    {
        if (id <= 0) return NotFound();
        PreparePage();
        return Page();
    }
}
