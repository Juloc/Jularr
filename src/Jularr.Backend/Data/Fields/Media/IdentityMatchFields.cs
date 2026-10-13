using Jularr.Backend.Data.Enums.Media;

namespace Jularr.Backend.Data.Fields.Media
{
    public static class IdentityMatchFields
    {
        public static readonly EnumField IdentityMatchStatusTypeId = new(typeof(IdentityMatchStatusType));
        public static readonly StringField Candidates = new() { AllowBlank = false };
        public static readonly StringField Seasons = new() { AllowBlank = false };
        public static readonly IntField ProviderId = new() { Minimum = 1 };
        public static readonly IntField Limit = new() { Minimum = 1, Maximum = 100 };
        public static readonly IntField SeasonNumber = new() { Minimum = 1 };
        public static readonly IntField Episodes = new() { Minimum = 0 };
        public static readonly IntArrayField CandidateIds = new();
        public static readonly StringArrayField CandidateLabels = new();
        public static readonly BoolField IsDismissed = new();
        public static readonly BoolField IsManual = new();
    }
}
