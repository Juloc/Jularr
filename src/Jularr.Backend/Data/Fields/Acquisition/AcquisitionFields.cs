using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class AcquisitionConnectionFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false, Trim = true };
        public static readonly EnumField AcquisitionConnectionTypeId = new(typeof(AcquisitionConnectionType));
        public static readonly StringField BaseUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 500, AllowBlank = false, Trim = true, TrimsTrailingSlash = true, Format = StringFormat.HttpUrl };
        public static readonly StringField Host = new(NpgsqlDbType.Varchar) { MaximumLength = 253, Required = false, AllowBlank = false, Trim = true, Format = StringFormat.HostName };
        public static readonly IntField Port = new() { Minimum = 1, Maximum = 65535, Required = false };
        public static readonly BoolField UseSsl = new();
        public static readonly StringField ApiKey = new() { MaximumLength = 256, Required = false, Format = StringFormat.SingleLine };
        public static readonly StringField Username = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false, Trim = true, Format = StringFormat.SingleLine };
        public static readonly ByteArrayField ProtectedApiKey = new();
        public static readonly StringField Password = new() { MaximumLength = 256, Required = false, Format = StringFormat.SingleLine };
        public static readonly ByteArrayField ProtectedPassword = new();
        public static readonly EnumField AcquisitionConnectionRoleId = new(typeof(AcquisitionConnectionRole));
        public static readonly BoolField HasApiKey = new();
        public static readonly BoolField IsEnabled = new();
        public static readonly IntField Priority = new() { Minimum = 1, Maximum = 100 };
        // Empty categories use the media type ranges.
        public static readonly IntArrayField Categories = new() { Minimum = 1, Maximum = 999999, MaximumLength = 50, Required = false, IsSet = true };
        public static readonly StringField DownloadCategory = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false, Trim = true, Format = StringFormat.SingleLine };
        public static readonly StringField RemotePath = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, Required = false, Trim = true, Format = StringFormat.SingleLine };
        public static readonly StringField LocalPath = new(NpgsqlDbType.Varchar) { MaximumLength = 1024, Required = false, Trim = true, Format = StringFormat.SingleLine };
        public static readonly LongField DownloadStorageMountId = new() { Minimum = 1, Required = false };
        public static readonly IntField MaxConnections = new() { Minimum = 1, Maximum = 50, Required = false };
        public static readonly StringField Capabilities = new() { Required = false };
        public static readonly StringArrayField SearchModes = new() { ItemMaximumLength = 32 };
        public static readonly StringField ServerVersion = new() { MaximumLength = 64, Required = false };
        public static readonly DateTimeField CheckedAt = new() { Required = false };
        public static readonly IntArrayField MediaTypeIds = new() { Minimum = 1, Maximum = 7, MaximumLength = 5, Required = false, IsSet = true };
        public static readonly BoolField SearchesAutomatically = new();
        public static readonly IntField QueryLimit = new() { Minimum = 1, Maximum = 100000, Required = false };
        public static readonly IntField GrabLimit = new() { Minimum = 1, Maximum = 100000, Required = false };
        public static readonly IntField QueryCount = new() { Minimum = 0 };
        public static readonly IntField GrabCount = new() { Minimum = 0 };
        public static readonly DateTimeField RetryAt = new() { Required = false };
        public static readonly BoolField IsBackedOff = new();
        public static readonly StringField ErrorKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly BoolField IsAvailable = new();
        public static readonly LongField Version = new() { Minimum = 1 };
        public static readonly StringField SearchQuery = new() { MaximumLength = 200, AllowBlank = false, Trim = true, Format = StringFormat.SingleLine };
    }

    public static class AcquisitionReleaseFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField ReleaseIdentity = new(NpgsqlDbType.Varchar) { MaximumLength = 600, AllowBlank = false };
        public static readonly StringArrayField IndexerFlags = new() { MaximumLength = 32, ItemMaximumLength = 128 };
        public static readonly StringArrayField ReleaseIdentities = new() { ItemMaximumLength = 600 };
        public static readonly StringField Title = new(NpgsqlDbType.Varchar) { MaximumLength = 1000, AllowBlank = false };
        public static readonly StringField SourceUrl = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false, Format = StringFormat.HttpUrl };
        public static readonly StringField QualityKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly EnumField AcquisitionReleaseStatusTypeId = new(typeof(AcquisitionReleaseStatusType));
        public static readonly StringField ReasonKey = new(NpgsqlDbType.Varchar) { MaximumLength = 64, Required = false };
        public static readonly StringField CandidateId = new() { MaximumLength = 600, AllowBlank = false };
        public static readonly EnumField ReleaseDecisionTypeId = new(typeof(ReleaseDecisionType));
        public static readonly IntField Score = new();
        public static readonly StringField ReasonCode = new() { MaximumLength = 64, AllowBlank = false };
        public static readonly StringField ReasonValue = new() { MaximumLength = 1000, Required = false };
        public static readonly IntField ProgressPercent = new() { Minimum = 0, Maximum = 100, Required = false };
        public static readonly StringField Progress = new() { AllowBlank = false };
        public static readonly StringField ExternalId = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false };
        public static readonly IntField ParentUnitNumber = new() { Minimum = 0, Maximum = 32767, Required = false };
        public static readonly IntField UnitStart = new() { Minimum = 0, Maximum = 32767, Required = false };
        public static readonly IntField UnitEnd = new() { Minimum = 0, Maximum = 32767, Required = false };
        public static readonly LongField SizeBytes = new() { Minimum = 0, Required = false };
        public static readonly DateTimeField PublishedAt = new() { Required = false };
        public static readonly DateTimeField CreatedAt = new();
        public static readonly BoolField IsBlocked = new();
    }

    public static class AcquisitionProfileFields
    {
        public static readonly LongField Id = new() { Minimum = 1 };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 128, AllowBlank = false, Trim = true };
        public static readonly StringField Definition = new() { MaximumLength = 65536, AllowBlank = false };
        public static readonly BoolField IsDefault = new();
        public static readonly StringArrayField AllowedQualities = new() { ItemMaximumLength = 128, MaximumLength = 32 };
        public static readonly BoolField UpgradeAllowed = new();
        public static readonly StringField UpgradeCutoffQuality = new() { MaximumLength = 128, Required = false };
        public static readonly IntField MinimumSizeMb = new() { Minimum = 0, Maximum = 1_000_000, Required = false };
        public static readonly IntField MaximumSizeMb = new() { Minimum = 1, Maximum = 1_000_000, Required = false };
        public static readonly IntField WantedCount = new() { Minimum = 0 };
        public static readonly LongField Version = new() { Minimum = 1 };

        // Taken-over monitoring: release types and statuses, book languages and least pages.
        public static readonly StringArrayField MonitoredValues = new() { ItemMaximumLength = 64, MaximumLength = 60 };
        public static readonly IntField MinimumPages = new() { Minimum = 1, Required = false };

    }

    // Suggested sources of the catalog page.
    public static class SourceCatalogFields
    {
        public static readonly StringField Key = new() { MaximumLength = 64, AllowBlank = false };
        public static readonly StringField Name = new() { MaximumLength = 128, AllowBlank = false };
        public static readonly EnumField SourceGroupId = new(typeof(SourceGroup));
        public static readonly EnumField SourceAvailabilityId = new(typeof(SourceAvailability));
        public static readonly StringArrayField Capabilities = new() { ItemMaximumLength = 64 };
        public static readonly StringField DefaultBaseUrl = new() { MaximumLength = 500, Required = false };
        public static readonly BoolField IsConfigured = new();
        public static readonly StringArrayField Keys = new() { ItemMaximumLength = 64 };
    }
}
