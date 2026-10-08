using Jularr.Web.Features.Discovery;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Home;

/// <summary>Where a signed-in visit without a destination lands.</summary>
public enum HomeLanding
{
    Home,
    Library
}

/// <summary>
/// A stored Home/Discover layout, the same shape for the instance default and a profile override. Media types are the stable storage identifiers
/// (<see cref="WorkMediaTypes.ToStorage"/>), never labels, and unknown identifiers survive untouched so a type that disappears and returns keeps its place.
/// </summary>
public sealed record HomeLayoutPreference(IReadOnlyList<string> MediaOrder, IReadOnlyList<string> Hidden, HomeLanding Landing, bool PrioritizeContinue)
{
    /// <summary>What an instance that never configured anything uses: the order Home had before it became configurable.</summary>
    public static HomeLayoutPreference BuiltIn { get; } = new([.. HomeLayoutPolicy.DefaultOrder.Select(WorkMediaTypes.ToStorage)], [], HomeLanding.Home, true);
}

/// <summary>The layout one viewer sees: only available media types, in order, with the viewer's hidden types marked.</summary>
public sealed record EffectiveHomeLayout(IReadOnlyList<WorkMediaType> Order, IReadOnlySet<WorkMediaType> Hidden, HomeLanding Landing, bool PrioritizeContinue, bool IsCustomized, HomeOnboardingState Onboarding)
{
    /// <summary>The types that get a group on Home/Discover and an item in the media-type bar, in order.</summary>
    public IReadOnlyList<WorkMediaType> Shown => [.. Order.Where(type => !Hidden.Contains(type))];
}

/// <summary>The pure rules that turn stored preferences and the available media types into what a viewer sees and into what an edit stores.</summary>
public static class HomeLayoutPolicy
{
    /// <summary>The media types Home/Discover can group: the ones with a discovery feed, in the order of the built-in layout.</summary>
    public static IReadOnlyList<WorkMediaType> DefaultOrder { get; } =
        [WorkMediaType.Anime, WorkMediaType.Series, WorkMediaType.Movie, WorkMediaType.LightNovel, WorkMediaType.Book, WorkMediaType.Manga];

    /// <summary>
    /// The effective layout: stored types that are available keep their stored order, available types the stored list does not know yet are
    /// appended in the default order, and everything else (a switched-off module, a stale identifier) is left out of the view but never deleted from the store.
    /// </summary>
    public static EffectiveHomeLayout Resolve(HomeLayoutPreference preference, IReadOnlySet<WorkMediaType> available, bool isCustomized, HomeOnboardingState onboarding = HomeOnboardingState.Skipped)
    {
        var order = OrderOf(preference.MediaOrder, available);
        var hidden = preference.Hidden.Select(WorkMediaTypes.Parse).OfType<WorkMediaType>().Where(order.Contains).ToHashSet();
        return new EffectiveHomeLayout(order, hidden, preference.Landing, preference.PrioritizeContinue, isCustomized, onboarding);
    }

    /// <summary>
    /// What an edit stores. The editor only shows the available types, so a type that is currently unavailable keeps its slot and its hidden flag from
    /// <paramref name="stored"/>: switching a module off and on never loses the viewer's arrangement.
    /// </summary>
    public static HomeLayoutPreference Merge(HomeLayoutPreference stored, HomeLayoutPreference editedPreference, IReadOnlySet<WorkMediaType> available)
    {
        var edited = new Queue<WorkMediaType>(editedPreference.MediaOrder.Select(WorkMediaTypes.Parse).OfType<WorkMediaType>().Where(available.Contains).Distinct());
        var order = new List<string>();
        foreach (var identifier in stored.MediaOrder.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var type = WorkMediaTypes.Parse(identifier);
            if (type is { } known && available.Contains(known))
            {
                if (edited.Count > 0)
                {
                    order.Add(WorkMediaTypes.ToStorage(edited.Dequeue()));
                }
            }
            else
            {
                order.Add(identifier);
            }
        }

        order.AddRange(edited.Select(WorkMediaTypes.ToStorage));
        var hidden = stored.Hidden.Where(identifier => WorkMediaTypes.Parse(identifier) is not { } type || !available.Contains(type)).ToList();
        hidden.AddRange(editedPreference.Hidden.Select(WorkMediaTypes.Parse).OfType<WorkMediaType>().Where(available.Contains).Select(WorkMediaTypes.ToStorage));
        return new HomeLayoutPreference(order, [.. hidden.Distinct(StringComparer.OrdinalIgnoreCase)], editedPreference.Landing, editedPreference.PrioritizeContinue);
    }

    /// <summary>The preference an editor form posts: the card order, the checked cards (every other available type is hidden), the landing page and the Continue switch, merged into what is stored.</summary>
    public static HomeLayoutPreference FromEditor(HomeLayoutPreference stored, IEnumerable<string>? order, IEnumerable<string>? shown, string? landing, bool prioritizeContinue, IReadOnlySet<WorkMediaType> available)
    {
        var shownTypes = (shown ?? []).Select(WorkMediaTypes.Parse).OfType<WorkMediaType>().ToHashSet();
        var hidden = available.Where(type => !shownTypes.Contains(type)).Select(WorkMediaTypes.ToStorage);
        return Merge(stored, new HomeLayoutPreference([.. order ?? []], [.. hidden], landing == "library" ? HomeLanding.Library : HomeLanding.Home, prioritizeContinue), available);
    }

    /// <summary>A quick preset: the types it names come first in the given order and are shown, every other available type stays after them hidden. A preset changes the editor state only.</summary>
    public static (IReadOnlyList<WorkMediaType> Order, IReadOnlySet<WorkMediaType> Hidden) ApplyPreset(IReadOnlyList<WorkMediaType> presetOrder, IReadOnlySet<WorkMediaType> available)
    {
        var shown = presetOrder.Where(available.Contains).ToList();
        var rest = DefaultOrder.Where(type => available.Contains(type) && !shown.Contains(type)).ToList();
        return ([.. shown, .. rest], rest.ToHashSet());
    }

    private static List<WorkMediaType> OrderOf(IReadOnlyList<string> stored, IReadOnlySet<WorkMediaType> available)
    {
        var order = stored.Select(WorkMediaTypes.Parse).OfType<WorkMediaType>().Where(available.Contains).Distinct().ToList();
        order.AddRange(DefaultOrder.Where(type => available.Contains(type) && !order.Contains(type)));
        return order;
    }
}
