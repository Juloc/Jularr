using System.Collections.Frozen;

namespace Jularr.Web.Features.Events;

/// <summary>
/// Every meaningful domain event Jularr can raise (#429). Features publish one of these through
/// <see cref="IJularrEventPublisher"/> instead of calling a notification channel directly, so
/// delivery, subscriptions and deduplication live in one place.
/// </summary>
public enum JularrEventCategory
{
    DownloadGrabbed = 1,
    DownloadFailed = 2,
    ImportCompleted = 3,
    ImportFailed = 4,
    ReleaseAvailable = 5,
    RequestApproved = 6,
    RequestDenied = 7,
    StorageProblem = 8
}

public enum JularrEventSeverity
{
    Info = 1,
    Warning = 2,
    Critical = 3
}

/// <summary>
/// Who an event is for. <see cref="Profile"/> events belong to the one profile named on the
/// event; <see cref="Admin"/> events (infrastructure/system problems) go to every owner/media
/// manager account instead, per the issue's "owner/admin errors are not exposed to normal
/// profiles" rule.
/// </summary>
public enum JularrEventAudience
{
    Profile = 1,
    Admin = 2
}

/// <summary>
/// Static notification policy for each event category. This is the one canonical catalog used by
/// routing and user settings; pages and sinks must not maintain independent copies.
/// </summary>
public static class JularrEventCategories
{
    public sealed record Meta(
        JularrEventAudience Audience,
        JularrEventSeverity Severity,
        string MessageKey,
        string LabelKey,
        NotificationTopicGroup TopicGroup,
        bool DefaultEnabled,
        FrozenSet<NotificationChannel> DefaultChannels,
        NotificationDeliveryTiming DefaultTiming,
        bool CanDisable,
        bool RequiresAtLeastOneRoute,
        FrozenSet<NotificationChannel> SupportedChannels,
        bool AllowsDigest,
        NotificationQuietHoursPolicy QuietHoursPolicy,
        bool AllowsTransientAttention)
    {
        public bool Supports(NotificationChannel channel) => SupportedChannels.Contains(channel);
    }

    private static readonly FrozenSet<NotificationChannel> AllProfileChannels =
        ChannelSet(NotificationChannel.InApp, NotificationChannel.Push, NotificationChannel.Email);

    public static readonly IReadOnlyDictionary<JularrEventCategory, Meta> All =
        new Dictionary<JularrEventCategory, Meta>
        {
            [JularrEventCategory.DownloadGrabbed] = ProfilePolicy(
                JularrEventSeverity.Info,
                "notifications.event.downloadGrabbed",
                "notifications.category.downloadGrabbed",
                NotificationTopicGroup.Media,
                allowsDigest: true,
                allowsTransientAttention: false),

            [JularrEventCategory.DownloadFailed] = ProfilePolicy(
                JularrEventSeverity.Warning,
                "notifications.event.downloadFailed",
                "notifications.category.downloadFailed",
                NotificationTopicGroup.Media,
                allowsDigest: false,
                allowsTransientAttention: true),

            [JularrEventCategory.ImportCompleted] = ProfilePolicy(
                JularrEventSeverity.Info,
                "notifications.event.importCompleted",
                "notifications.category.importCompleted",
                NotificationTopicGroup.Media,
                allowsDigest: true,
                allowsTransientAttention: true),

            [JularrEventCategory.ImportFailed] = ProfilePolicy(
                JularrEventSeverity.Warning,
                "notifications.event.importFailed",
                "notifications.category.importFailed",
                NotificationTopicGroup.Media,
                allowsDigest: false,
                allowsTransientAttention: true),

            [JularrEventCategory.ReleaseAvailable] = ProfilePolicy(
                JularrEventSeverity.Info,
                "notifications.event.releaseAvailable",
                "notifications.category.releaseAvailable",
                NotificationTopicGroup.Media,
                allowsDigest: true,
                allowsTransientAttention: true),

            [JularrEventCategory.RequestApproved] = ProfilePolicy(
                JularrEventSeverity.Info,
                "notifications.event.requestApproved",
                "notifications.category.requestApproved",
                NotificationTopicGroup.Requests,
                allowsDigest: true,
                allowsTransientAttention: true),

            [JularrEventCategory.RequestDenied] = ProfilePolicy(
                JularrEventSeverity.Info,
                "notifications.event.requestDenied",
                "notifications.category.requestDenied",
                NotificationTopicGroup.Requests,
                allowsDigest: true,
                allowsTransientAttention: true),

            [JularrEventCategory.StorageProblem] = new(
                Audience: JularrEventAudience.Admin,
                Severity: JularrEventSeverity.Critical,
                MessageKey: "notifications.event.storageProblem",
                LabelKey: "notifications.category.storageProblem",
                TopicGroup: NotificationTopicGroup.SystemAdmin,
                DefaultEnabled: true,
                DefaultChannels: ChannelSet(NotificationChannel.InApp),
                DefaultTiming: NotificationDeliveryTiming.Immediate,
                CanDisable: true,
                RequiresAtLeastOneRoute: false,
                SupportedChannels: AllProfileChannels,
                AllowsDigest: false,
                QuietHoursPolicy: NotificationQuietHoursPolicy.Bypass,
                AllowsTransientAttention: true)
        }.ToFrozenDictionary();

    static JularrEventCategories() => ValidateCatalog();

    public static Meta Of(JularrEventCategory category)
    {
        if (!Enum.IsDefined(category) || !All.TryGetValue(category, out var meta))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown Jularr event category.");
        }

        return meta;
    }

    /// <summary>
    /// Validates one occurrence against the canonical category policy. This boundary is used both by
    /// <see cref="JularrEvent.Create"/> and the publisher so manually constructed events cannot widen
    /// audience or change severity before they reach the durable EventLog.
    /// </summary>
    public static void ValidateOccurrence(
        JularrEventCategory category,
        JularrEventAudience audience,
        JularrEventSeverity severity,
        string? profileId)
    {
        var meta = Of(category);

        if (audience != meta.Audience)
        {
            throw new InvalidOperationException(
                $"{category} requires audience {meta.Audience}, not {audience}.");
        }

        if (severity != meta.Severity)
        {
            throw new InvalidOperationException(
                $"{category} requires severity {meta.Severity}, not {severity}.");
        }

        switch (meta.Audience)
        {
            case JularrEventAudience.Profile when string.IsNullOrWhiteSpace(profileId):
                throw new InvalidOperationException($"{category} requires an explicit profile id.");

            case JularrEventAudience.Admin when profileId is not null:
                throw new InvalidOperationException($"{category} is Admin-scoped and must not carry a profile id.");
        }
    }

    public static void ValidateOccurrence(JularrEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ValidateOccurrence(domainEvent.Category, domainEvent.Audience, domainEvent.Severity, domainEvent.ProfileId);
    }

    /// <summary>
    /// Validates catalog invariants once at startup/type initialization and is public so focused
    /// architecture tests can guard future category additions.
    /// </summary>
    public static void ValidateCatalog()
    {
        var categories = Enum.GetValues<JularrEventCategory>();

        if (All.Count != categories.Length || categories.Any(category => !All.ContainsKey(category)))
        {
            throw new InvalidOperationException("Every JularrEventCategory must have exactly one notification policy.");
        }

        foreach (var (category, meta) in All)
        {
            if (meta.SupportedChannels.Count == 0)
            {
                throw new InvalidOperationException($"{category} must support at least one notification channel.");
            }

            if (meta.DefaultChannels.Any(channel => !meta.SupportedChannels.Contains(channel)))
            {
                throw new InvalidOperationException($"{category} has a default channel that is not supported.");
            }

            if (meta.DefaultEnabled && meta.DefaultChannels.Count == 0)
            {
                throw new InvalidOperationException($"{category} is enabled by default but has no default channel.");
            }

            if (meta.DefaultTiming == NotificationDeliveryTiming.Digest && !meta.AllowsDigest)
            {
                throw new InvalidOperationException($"{category} defaults to Digest but does not allow Digest.");
            }

            if (!meta.CanDisable && !meta.DefaultEnabled)
            {
                throw new InvalidOperationException($"{category} is mandatory but disabled by default.");
            }

            if (meta.RequiresAtLeastOneRoute && meta.DefaultChannels.Count == 0)
            {
                throw new InvalidOperationException($"{category} requires a route but has no default channel.");
            }
        }
    }

    private static Meta ProfilePolicy(
        JularrEventSeverity severity,
        string messageKey,
        string labelKey,
        NotificationTopicGroup topicGroup,
        bool allowsDigest,
        bool allowsTransientAttention) =>
        new(
            Audience: JularrEventAudience.Profile,
            Severity: severity,
            MessageKey: messageKey,
            LabelKey: labelKey,
            TopicGroup: topicGroup,
            DefaultEnabled: true,
            DefaultChannels: ChannelSet(NotificationChannel.InApp),
            DefaultTiming: NotificationDeliveryTiming.Immediate,
            CanDisable: true,
            RequiresAtLeastOneRoute: false,
            SupportedChannels: AllProfileChannels,
            AllowsDigest: allowsDigest,
            QuietHoursPolicy: NotificationQuietHoursPolicy.Delayable,
            AllowsTransientAttention: allowsTransientAttention);

    private static FrozenSet<NotificationChannel> ChannelSet(params NotificationChannel[] channels) =>
        channels.ToFrozenSet();
}

/// <summary>
/// One canonical domain event (#429). <see cref="MessageParams"/> carries placeholder values for
/// <see cref="JularrEventCategories.Meta.MessageKey"/> (for example the media title) rather than
/// pre-rendered HTML, so every profile reads it in their own language.
/// </summary>
public sealed record JularrEvent(
    Guid Id,
    JularrEventCategory Category,
    JularrEventAudience Audience,
    string? ProfileId,
    string? MediaType,
    string? SubjectId,
    IReadOnlyDictionary<string, string>? MessageParams,
    JularrEventSeverity Severity,
    string? DeepLink,
    string? DedupKey,
    Guid? RelatedOperationId,
    DateTime CreatedAtUtc)
{
    /// <summary>
    /// Builds an event from its category's static metadata, so callers only supply what is
    /// specific to the occurrence.
    /// </summary>
    public static JularrEvent Create(
        JularrEventCategory category,
        string? profileId = null,
        string? mediaType = null,
        string? subjectId = null,
        IReadOnlyDictionary<string, string>? messageParams = null,
        string? deepLink = null,
        string? dedupKey = null,
        Guid? relatedOperationId = null)
    {
        var meta = JularrEventCategories.Of(category);
        JularrEventCategories.ValidateOccurrence(category, meta.Audience, meta.Severity, profileId);

        return new JularrEvent(
            Guid.NewGuid(),
            category,
            meta.Audience,
            profileId?.Trim(),
            mediaType,
            subjectId,
            messageParams,
            meta.Severity,
            deepLink,
            dedupKey,
            relatedOperationId,
            DateTime.UtcNow);
    }
}
