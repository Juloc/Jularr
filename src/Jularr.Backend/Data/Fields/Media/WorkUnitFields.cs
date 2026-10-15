using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Media
{
    // Seasons and episodes as a viewer sees them.
    public static class WorkUnitFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly IntField SeasonNumber = new() { Minimum = 0, Maximum = 9999 };
        public static readonly StringField Title = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly StringField Overview = new(NpgsqlDbType.Varchar) { MaximumLength = 4000, Required = false };
        public static readonly StringField ImageUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, Required = false };
        public static readonly IntField RuntimeMinutes = new() { Minimum = 1, Maximum = 1440, Required = false };
        public static readonly DateField ReleasedOn = new() { Required = false };
        public static readonly IntField EpisodeCount = new() { Minimum = 0 };
        public static readonly IntField AvailableCount = new() { Minimum = 0 };
        public static readonly IntField ReleaseYear = new() { Minimum = 1800, Maximum = 2200, Required = false };
        public static readonly BoolField IsUpcoming = new();
    }
}
