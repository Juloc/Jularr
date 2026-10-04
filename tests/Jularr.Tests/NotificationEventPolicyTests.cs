using Jularr.Web.Features.Events;

namespace Jularr.Tests;

[TestClass]
public sealed class NotificationEventPolicyTests
{
    [TestMethod]
    public void CatalogCoversEveryEventCategoryExactlyOnce()
    {
        JularrEventCategories.ValidateCatalog();

        var categories = Enum.GetValues<JularrEventCategory>();

        Assert.AreEqual(categories.Length, JularrEventCategories.All.Count);
        CollectionAssert.AreEquivalent(
            categories.Cast<object>().ToArray(),
            JularrEventCategories.All.Keys.Cast<object>().ToArray());
    }

    [TestMethod]
    public void EveryDefaultChannelIsSupportedByItsEvent()
    {
        foreach (var (category, policy) in JularrEventCategories.All)
        {
            Assert.IsTrue(
                policy.DefaultChannels.All(policy.SupportedChannels.Contains),
                $"{category} has a default channel outside SupportedChannels.");

            if (policy.DefaultTiming == NotificationDeliveryTiming.Digest)
            {
                Assert.IsTrue(policy.AllowsDigest, $"{category} defaults to Digest but forbids Digest.");
            }
        }
    }

    [TestMethod]
    public void RequestEventsBelongToRequestsTopicNotMedia()
    {
        Assert.AreEqual(
            NotificationTopicGroup.Requests,
            JularrEventCategories.Of(JularrEventCategory.RequestApproved).TopicGroup);
        Assert.AreEqual(
            NotificationTopicGroup.Requests,
            JularrEventCategories.Of(JularrEventCategory.RequestDenied).TopicGroup);
    }

    [TestMethod]
    public void StorageProblemIsCriticalAdminImmediateAndBypassesQuietHours()
    {
        var policy = JularrEventCategories.Of(JularrEventCategory.StorageProblem);

        Assert.AreEqual(JularrEventAudience.Admin, policy.Audience);
        Assert.AreEqual(JularrEventSeverity.Critical, policy.Severity);
        Assert.AreEqual(NotificationTopicGroup.SystemAdmin, policy.TopicGroup);
        Assert.AreEqual(NotificationDeliveryTiming.Immediate, policy.DefaultTiming);
        Assert.AreEqual(NotificationQuietHoursPolicy.Bypass, policy.QuietHoursPolicy);
        Assert.IsFalse(policy.AllowsDigest);
        Assert.IsTrue(policy.AllowsTransientAttention);
        CollectionAssert.AreEquivalent(
            new[] { NotificationChannel.InApp },
            policy.DefaultChannels.ToArray());
    }

    [TestMethod]
    public void FailureEventsDoNotAllowDigestButReleaseEventsDo()
    {
        Assert.IsFalse(JularrEventCategories.Of(JularrEventCategory.DownloadFailed).AllowsDigest);
        Assert.IsFalse(JularrEventCategories.Of(JularrEventCategory.ImportFailed).AllowsDigest);
        Assert.IsTrue(JularrEventCategories.Of(JularrEventCategory.ReleaseAvailable).AllowsDigest);
    }

    [TestMethod]
    public void CurrentProfileEventsDefaultToInAppImmediate()
    {
        foreach (var (category, policy) in JularrEventCategories.All.Where(entry => entry.Value.Audience == JularrEventAudience.Profile))
        {
            Assert.IsTrue(policy.DefaultEnabled, $"{category} should preserve the current opt-out default.");
            Assert.AreEqual(NotificationDeliveryTiming.Immediate, policy.DefaultTiming);
            CollectionAssert.AreEquivalent(
                new[] { NotificationChannel.InApp },
                policy.DefaultChannels.ToArray(),
                $"{category} should preserve the current In-App default.");
        }
    }
}
