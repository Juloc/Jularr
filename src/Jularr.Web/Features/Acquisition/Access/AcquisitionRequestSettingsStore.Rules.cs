namespace Jularr.Web.Features.Acquisition.Access;

public sealed partial class AcquisitionRequestSettingsStore
{
    public Task<AcquisitionRequestSettings> SaveProfileAsync(long? id, string name, string? description, RequestRuleValues values, long revision, CancellationToken cancellationToken) =>
        MutateAsync(settings =>
        {
            if (settings.Revision != revision)
            {
                throw new RequestRuleConflictException();
            }

            var configuration = settings.Rules;
            if (id is not null && !configuration.Profiles.Any(profile => profile.Id == id))
            {
                throw new ArgumentException("The request rule no longer exists.");
            }

            var profile = new RequestRuleProfile(id ?? configuration.NextId, name, description, values).Validate();
            var profiles = id is null ? configuration.Profiles.Append(profile).ToArray() : configuration.Profiles.Select(existing => existing.Id == id ? profile : existing).ToArray();
            return settings with { Rules = configuration with { Profiles = profiles, NextId = id is null ? checked(configuration.NextId + 1) : configuration.NextId } };
        }, cancellationToken);

    public Task<AcquisitionRequestSettings> DuplicateProfileAsync(long id, string name, long revision, CancellationToken cancellationToken) =>
        MutateAsync(settings =>
        {
            if (settings.Revision != revision)
            {
                throw new RequestRuleConflictException();
            }

            var configuration = settings.Rules;
            var source = configuration.Profiles.SingleOrDefault(profile => profile.Id == id) ?? throw new ArgumentException("Unknown request rule.");
            var duplicate = (source with { Id = configuration.NextId, Name = name }).Validate();
            return settings with { Rules = configuration with { Profiles = [.. configuration.Profiles, duplicate], NextId = checked(configuration.NextId + 1) } };
        }, cancellationToken);

    public Task<AcquisitionRequestSettings> SetDefaultProfileAsync(long id, long revision, CancellationToken cancellationToken) =>
        MutateAsync(settings =>
        {
            if (settings.Revision != revision)
            {
                throw new RequestRuleConflictException();
            }

            if (!settings.Rules.Profiles.Any(profile => profile.Id == id))
            {
                throw new ArgumentException("Unknown request rule.");
            }

            return settings with { Rules = settings.Rules with { DefaultId = id } };
        }, cancellationToken);

    public Task<AcquisitionRequestSettings> DeleteProfileAsync(long id, bool confirmReassignment, long revision, CancellationToken cancellationToken) =>
        MutateAsync(settings =>
        {
            if (settings.Revision != revision)
            {
                throw new RequestRuleConflictException();
            }

            var configuration = settings.Rules;
            if (id == configuration.DefaultId || !configuration.Profiles.Any(profile => profile.Id == id))
            {
                throw new ArgumentException("The default or an unknown request rule cannot be deleted.");
            }

            if (!confirmReassignment && configuration.Users.Values.Any(user => user.RuleId == id))
            {
                throw new ArgumentException("Confirm reassignment of the users of this request rule.");
            }

            var users = configuration.Users.ToDictionary(pair => pair.Key, pair => pair.Value.RuleId == id ? pair.Value with { RuleId = configuration.DefaultId } : pair.Value);
            return settings with { Rules = configuration with { Profiles = configuration.Profiles.Where(profile => profile.Id != id).ToArray(), Users = users } };
        }, cancellationToken);

    public Task<AcquisitionRequestSettings> SaveUserRuleAsync(string userId, long? ruleId, RequestRuleValues edited, bool useOverrides, long revision, CancellationToken cancellationToken) =>
        MutateAsync(settings =>
        {
            if (settings.Revision != revision)
            {
                throw new RequestRuleConflictException();
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(userId);
            var configuration = settings.Rules;
            var profile = configuration.Profiles.SingleOrDefault(profile => profile.Id == (ruleId ?? configuration.DefaultId)) ?? throw new ArgumentException("Unknown request rule.");
            var overrides = useOverrides ? RequestRuleOverrides.Difference(edited, profile.Values) : new RequestRuleOverrides();
            var users = configuration.Users.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (ruleId is null && !overrides.HasChanges)
            {
                users.Remove(userId);
            }
            else
            {
                users[userId] = new(ruleId, overrides);
            }

            return settings with { Rules = configuration with { Users = users } };
        }, cancellationToken);

    public Task<AcquisitionRequestSettings> FinishApprovalTransitionAsync(long revision, CancellationToken cancellationToken) =>
        MutateAsync(settings => settings.Revision == revision
            ? settings with { Rules = settings.Rules with { HasApprovalTransition = false } }
            : throw new RequestRuleConflictException(), cancellationToken);
}
