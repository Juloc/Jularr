namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>The status tabs of Admin → Requests.</summary>
public enum AdminRequestTab
{
    All,

    /// <summary>Waiting for the owner's decision.</summary>
    Open,

    /// <summary>Approved requests awaiting acquisition.</summary>
    Approved,
    Failed,

    /// <summary>Searching, downloading or importing.</summary>
    InProgress,

    Done,
    Rejected
}

/// <summary>Queue filters. Lifecycle counts share the same title, media, language and requester scope, independently of the active tab and technical status filter.</summary>
public sealed record AdminRequestFilter(
    AdminRequestTab Tab = AdminRequestTab.All,
    IReadOnlyList<MediaAcquisitionKind>? Kinds = null,
    IReadOnlyList<AcquisitionRequestStatus>? Statuses = null,
    IReadOnlyList<string>? Languages = null,
    IReadOnlyList<string>? RequesterProfileIds = null,
    string? Search = null,
    int Page = 1,
    int PageSize = AdminRequestQuery.DefaultPageSize,
    string Sort = "newest",
    int? Season = null)
{
    public bool HasNarrowing =>
        Kinds is { Count: > 0 } || Statuses is { Count: > 0 } || Languages is { Count: > 0 } || RequesterProfileIds is { Count: > 0 }
        || !string.IsNullOrWhiteSpace(Search) || Sort != "newest" || Season is not null;
}

/// <summary>One page of the owner's request queue plus what the filter bar and the tabs need.</summary>
public sealed record AdminRequestPage(
    IReadOnlyList<AcquisitionRequest> Items,
    AdminRequestFilter Filter,
    IReadOnlyDictionary<AdminRequestTab, int> TabCounts,
    IReadOnlyList<string> Languages,
    IReadOnlyList<int> Seasons,
    int Total,
    int PageCount)
{
    public int Page => Filter.Page;

    public int PageSize => Filter.PageSize;

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < PageCount;
}

/// <summary>Shared queue address normalization and lifecycle tab mapping.</summary>
public static class AdminRequestQuery
{
    public const int DefaultPageSize = 50;
    public static readonly IReadOnlyList<int> StandardPageSizes = [20, 50, 100, 200];

    public static string NormalizeSort(string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        "oldest" => "oldest",
        "modified" => "modified",
        "title" => "title",
        "requester" => "requester",
        _ => "newest"
    };

    public static int NormalizePageSize(int pageSize) => pageSize is >= 1 and <= 500 ? pageSize : DefaultPageSize;

    public static AdminRequestTab TabOf(AcquisitionRequestStatus status) => status switch
    {
        AcquisitionRequestStatus.Pending => AdminRequestTab.Open,
        AcquisitionRequestStatus.Approved => AdminRequestTab.Approved,
        AcquisitionRequestStatus.Failed => AdminRequestTab.Failed,
        AcquisitionRequestStatus.Searching or AcquisitionRequestStatus.Downloading or AcquisitionRequestStatus.Importing
            => AdminRequestTab.InProgress,
        AcquisitionRequestStatus.Completed => AdminRequestTab.Done,
        AcquisitionRequestStatus.Rejected => AdminRequestTab.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    /// <summary>The audio language a request asks for; only anime requests carry one.</summary>
    public static string? LanguageOf(AcquisitionRequest request) => request.Options.AudioLanguage;

    public static string TabName(AdminRequestTab tab) => tab switch
    {
        AdminRequestTab.Open => "open",
        AdminRequestTab.Approved => "approved",
        AdminRequestTab.InProgress => "progress",
        AdminRequestTab.Failed => "failed",
        AdminRequestTab.Done => "done",
        AdminRequestTab.Rejected => "rejected",
        _ => "all"
    };

    public static AdminRequestTab ParseTab(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "open" => AdminRequestTab.Open,
        "approved" => AdminRequestTab.Approved,
        "progress" => AdminRequestTab.InProgress,
        "failed" => AdminRequestTab.Failed,
        "done" => AdminRequestTab.Done,
        "rejected" => AdminRequestTab.Rejected,
        _ => AdminRequestTab.All
    };

    /// <summary>Reads a media type from the address; an unknown value means no filter.</summary>
    public static MediaAcquisitionKind? TryParseKind(string? value)
    {
        foreach (var kind in Enum.GetValues<MediaAcquisitionKind>())
        {
            if (string.Equals(AcquisitionAccessNames.Kind(kind), value, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>Reads a request status from the address; an unknown value means no filter.</summary>
    public static AcquisitionRequestStatus? TryParseStatus(string? value)
    {
        foreach (var status in Enum.GetValues<AcquisitionRequestStatus>())
        {
            if (string.Equals(AcquisitionAccessNames.Status(status), value, StringComparison.Ordinal))
            {
                return status;
            }
        }

        return null;
    }

    public static AdminRequestPage Empty(AdminRequestFilter filter) => new([], filter, Enum.GetValues<AdminRequestTab>().ToDictionary(tab => tab, _ => 0), [], [], 0, 1);
}
