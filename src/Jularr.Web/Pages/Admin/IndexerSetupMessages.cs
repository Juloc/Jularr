using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Localization;

namespace Jularr.Web.Pages.Admin;

/// <summary>The one wording of an indexer setup, refresh or search check for the owner, shared by the add/edit page and the Usenet hub.</summary>
public static class IndexerSetupMessages
{
    /// <summary>The sentence for a result and whether it is a problem the owner should see as an error.</summary>
    public static (string Text, bool IsError) Describe(UiTextBundle ui, IndexerSetupResult result)
    {
        var detail = result.Detail ?? string.Empty;
        var name = result.Entry?.Name ?? string.Empty;
        switch (result.Outcome)
        {
            case IndexerSetupOutcome.NotFound:
                return (ui["settings.indexers.notFound"], true);
            case IndexerSetupOutcome.Duplicate:
                return (ui.Format("settings.indexers.setup.duplicate", ("name", name)), true);
            case IndexerSetupOutcome.InvalidAddress:
                return (ui.Format("settings.indexers.setup.invalidAddress", ("detail", detail)), true);
            case IndexerSetupOutcome.ConnectionFailed:
                return (ui.Format($"settings.indexers.setup.failed.{Key(result.State)}", ("detail", detail)), true);
        }

        return result.State switch
        {
            IndexerCheckState.Valid or IndexerCheckState.NoResults when result.Outcome == IndexerSetupOutcome.Added && result.Entry?.Enabled != true => (ui.Format("settings.indexers.setup.addedOff", ("name", name), ("detail", detail)), true),
            IndexerCheckState.Valid or IndexerCheckState.NoResults => (ui.Format($"settings.indexers.setup.{Key(result.Outcome)}", ("name", name), ("detail", detail)), false),
            _ => (ui.Format("settings.indexers.setup.searchProblem", ("name", name), ("state", ui[$"settings.indexers.check.{Key(result.State)}"]), ("detail", detail)), true)
        };
    }

    /// <summary>The lower-camel key segment of an enum value, for example <c>authenticationFailed</c>.</summary>
    public static string Key<T>(T value) where T : struct, Enum => $"{char.ToLowerInvariant(value.ToString()[0])}{value.ToString()[1..]}";
}
