using System.Globalization;
using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.MediaCore;
using Jularr.Web.Features.Shell;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Web.Pages.Library;

public sealed class SeriesDetailModel(
    AppDbContext db,
    CurrentAccountContext account,
    IAppShellService appShell,
    VideoDetailQuery query,
    AcquisitionRequestService requests) : VideoDetailPageModel(db, account, appShell, query, requests)
{
    protected override WorkMediaType MediaType => WorkMediaType.Series;

    /// <summary>The season rail: regular seasons first, the specials last.</summary>
    public IReadOnlyList<AnimeStructureEntry> Seasons { get; private set; } = [];

    public AnimeStructureEntry? Selected { get; private set; }

    /// <summary>The episodes of the selected season in the chosen order.</summary>
    public IReadOnlyList<VideoDetailEpisode> SelectedEpisodes { get; private set; } = [];

    /// <summary>Episodes of the selected season without a file and without an open request.</summary>
    public IReadOnlyList<VideoDetailEpisode> MissingInSelected { get; private set; } = [];

    public AnimeEpisodeSort Sort { get; private set; }

    public AnimeEpisodeLayout Layout { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid workId, int? season, string? sort, string? view, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(workId, cancellationToken))
        {
            return NotFound();
        }

        Sort = AnimeDetailView.ParseSort(sort);
        Layout = AnimeDetailView.ParseLayout(view);
        Seasons = AnimeDetailView.SeasonEntries(Detail.Episodes.Select(x => x.SeasonNumber));
        var nextSeason = Detail.Next is { } next ? Detail.Episodes.First(x => x.Id == next.EpisodeId).SeasonNumber : (int?)null;
        Selected = AnimeDetailView.SelectEntry(Seasons, SeasonKey(season), SeasonKey(nextSeason));
        var inSeason = Detail.Episodes.Where(x => Selected is not null && x.SeasonNumber == Selected.SeasonNumber).ToArray();
        SelectedEpisodes = AnimeDetailView.Sort(inSeason, Sort, x => x.Number, x => x.IsWatched);
        MissingInSelected = VideoDetailView.RequestableEpisodes(inSeason);
        return Page();
    }

    /// <summary>The address of this page for a season, order and layout; only what differs from the defaults is written.</summary>
    public string PageHref(string? seasonKey, AnimeEpisodeSort sort, AnimeEpisodeLayout layout)
    {
        List<string> parts = [];
        if (seasonKey is { Length: > 1 })
        {
            parts.Add("season=" + seasonKey[1..]);
        }

        if (AnimeDetailView.SortName(sort) is { } sortValue)
        {
            parts.Add("sort=" + sortValue);
        }

        if (AnimeDetailView.LayoutName(layout) is { } layoutValue)
        {
            parts.Add("view=" + layoutValue);
        }

        return $"/Library/Series/{Detail.WorkId}{(parts.Count == 0 ? "" : "?" + string.Join('&', parts))}#episodes";
    }

    private static string? SeasonKey(int? season) => season is { } number ? "s" + number.ToString(CultureInfo.InvariantCulture) : null;
}
