using Jularr.Web.Features.InstantPlay;
using Jularr.Web.Features.Library;

namespace Jularr.Web.Pages.Library;

/// <summary>
/// One playback intent control of a Movie or Series page: the hero action (<paramref name="Episode"/> is the episode a Series targets,
/// null for a Movie) or the compact action of one episode row.
/// </summary>
public sealed record InstantPlayActionView(VideoDetailPageModel Page, PrimaryAction Action, VideoDetailEpisode? Episode, bool Compact);
