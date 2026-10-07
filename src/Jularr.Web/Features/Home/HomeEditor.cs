using Jularr.Web.Features.Localization;
using Jularr.Web.Features.MediaCore;

namespace Jularr.Web.Features.Home;

/// <summary>One media type in the editor: the card the viewer orders and shows or hides.</summary>
public sealed record HomeEditorItem(WorkMediaType Type, string LabelKey, string Icon, bool Shown)
{
    public string Id => WorkMediaTypes.ToStorage(Type);
}

/// <summary>A quick arrangement. It only fills the editor; nothing is stored until the viewer saves.</summary>
public sealed record HomePreset(string Id, string NameKey, IReadOnlyList<WorkMediaType> Order);

/// <summary>What the shared editor renders, for the instance default (Admin), a viewer's own layout (Settings) and the first-login onboarding alike.</summary>
public sealed record HomeEditorModel(UiTextBundle Ui, string IdPrefix, IReadOnlyList<HomeEditorItem> Items, HomeLanding Landing, bool PrioritizeContinue, IReadOnlyList<HomePreset> Presets)
{
    /// <summary>The cards for the available types in the layout's order; a hidden type keeps its place and is only unchecked.</summary>
    public static IReadOnlyList<HomeEditorItem> ItemsOf(IReadOnlyList<WorkMediaType> order, IReadOnlySet<WorkMediaType> hidden) =>
        [.. order.Select(type => new HomeEditorItem(type, LabelKey(type), Icon(type), !hidden.Contains(type)))];

    /// <summary>The presets that mean something here: one that names at least one available media type, so a preset never offers a type the instance does not serve.</summary>
    public static IReadOnlyList<HomePreset> PresetsFor(IReadOnlySet<WorkMediaType> available) => [.. AllPresets.Where(preset => preset.Order.Any(available.Contains))];

    public static IReadOnlyList<HomePreset> AllPresets { get; } =
    [
        new("balanced", "home.layout.preset.balanced", [WorkMediaType.Series, WorkMediaType.Movie, WorkMediaType.Anime, WorkMediaType.Manga, WorkMediaType.Book]),
        new("moviesSeries", "home.layout.preset.moviesSeries", [WorkMediaType.Movie, WorkMediaType.Series]),
        new("animeManga", "home.layout.preset.animeManga", [WorkMediaType.Anime, WorkMediaType.Manga, WorkMediaType.LightNovel]),
        new("booksNovels", "home.layout.preset.booksNovels", [WorkMediaType.Book, WorkMediaType.LightNovel]),
        new("everything", "home.layout.preset.everything", HomeLayoutPolicy.DefaultOrder)
    ];

    public static string LabelKey(WorkMediaType type) => type switch
    {
        WorkMediaType.Anime => "discover.categories.anime",
        WorkMediaType.Series => "search.type.series",
        WorkMediaType.Movie => "discover.categories.movies",
        WorkMediaType.LightNovel => "discover.categories.lightNovel",
        WorkMediaType.Book => "discover.categories.books",
        WorkMediaType.Manga => "reading.manga.title",
        _ => "discover.categories.all"
    };

    private static string Icon(WorkMediaType type) => type switch
    {
        WorkMediaType.Anime => "screen-play",
        WorkMediaType.Series => "tv",
        WorkMediaType.Movie => "film",
        WorkMediaType.LightNovel => "document",
        _ => "book"
    };
}
