using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields.Acquisition
{
    public static class DownloadClientConsoleFields
    {
        public static readonly StringField JobId = new(NpgsqlDbType.Varchar) { MaximumLength = 200, AllowBlank = false, Trim = true, Format = StringFormat.SingleLine };
        public static readonly StringField Name = new(NpgsqlDbType.Varchar) { MaximumLength = 1000, AllowBlank = false };
        public static readonly StringField Status = new(NpgsqlDbType.Varchar) { MaximumLength = 128, Required = false };
        public static readonly StringField Action = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false, Trim = true };
        public static readonly StringField Path = new(NpgsqlDbType.Varchar) { MaximumLength = 2048, Required = false };
        public static readonly StringField Folder = new(NpgsqlDbType.Varchar) { MaximumLength = 20, AllowBlank = false };
        public static readonly StringField FileName = new(NpgsqlDbType.Varchar) { MaximumLength = 256, AllowBlank = false };
        public static readonly StringField Assignment = new(NpgsqlDbType.Varchar) { MaximumLength = 32, AllowBlank = false };
        public static readonly LongField Bytes = new() { Minimum = 0, Required = false };
        public static readonly IntField ProgressPercent = new() { Minimum = 0, Maximum = 100, Required = false };
        public static readonly IntField TimeLeftSeconds = new() { Minimum = 0, Required = false };
        public static readonly BoolField Flag = new();
        public static readonly StringArrayField Categories = new() { MaximumLength = 200, ItemMaximumLength = 64 };
        public static readonly StringArrayField OrphanFolders = new() { MaximumLength = 200, ItemMaximumLength = 200 };
    }
}
