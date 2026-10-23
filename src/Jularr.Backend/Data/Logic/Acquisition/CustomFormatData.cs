using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Acquisition;

namespace Jularr.Backend.Data.Logic.Acquisition
{
    // A custom format as Sonarr and Radarr have it; profiles give it a score.
    internal sealed class CustomFormat
    {
        public CustomFormat(long id, string name, bool includeWhenRenaming, FormatCondition[] conditions)
        {
            Id = id;
            Name = name;
            IncludeWhenRenaming = includeWhenRenaming;
            Conditions = conditions;
        }

        public long Id { get; }
        public string Name { get; }
        public bool IncludeWhenRenaming { get; }
        public FormatCondition[] Conditions { get; }
    }

    // One condition of a format; its value depends on its kind, a negated one counts when it does not match.
    internal sealed class FormatCondition : BaseData
    {
        public FormatCondition(string name, FormatConditionKind kind, bool negate, bool required)
        {
            Name = name;
            Kind = kind;
            Negate = negate;
            Required = required;
        }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Name))]
        public string Name { get; }
        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Kind))]
        public FormatConditionKind Kind { get; }
        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Negate))]
        public bool Negate { get; }
        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Required))]
        public bool Required { get; }

        // A pattern for title, edition and group; a two-letter language or "original"; a source, quality modifier, indexer flag or release type.
        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Value))]
        public string? Value { get; init; }

        // A language condition that matches any other language.
        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.ExceptLanguage))]
        public bool ExceptLanguage { get; init; }

        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Resolution))]
        public int? Resolution { get; init; }

        // Size in gigabytes or a year.
        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Minimum))]
        public decimal? Minimum { get; init; }
        [DataField(typeof(FormatConditionFields), nameof(FormatConditionFields.Maximum))]
        public decimal? Maximum { get; init; }
    }

    // A format's score in one profile.
    internal sealed record FormatScore(long FormatId, int Score);
}
