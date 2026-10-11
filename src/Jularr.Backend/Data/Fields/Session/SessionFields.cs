using Jularr.Backend.Data.Fields;

namespace Jularr.Backend.Data.Fields.Session
{
    public static class SessionFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly GuidField PublicId = new();
        public static readonly LongField AccountId = new() { Minimum = 1 };
        public static readonly LongField ActiveProfileId = new() { Minimum = 1, Required = false };
        public static readonly ByteArrayField TokenHash = new() { MaximumLength = 32, MinimumLength = 32 };
        public static readonly DateTimeField ExpiresAt = new();
        public static readonly DateTimeField RevokedAt = new() { Required = false };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly DateTimeField LastSeenAt = new() { Required = false };
        public static readonly LongField CredentialRevision = new() { Minimum = 1 };
        public static readonly LongField RotationRevision = new() { Minimum = 1 };
        public static readonly DateTimeField RotatedAt = new() { Required = false };
    }
}
