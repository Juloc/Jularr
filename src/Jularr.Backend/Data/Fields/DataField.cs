using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Field;
using NpgsqlTypes;

namespace Jularr.Backend.Data.Fields
{
    public abstract class DataField
    {
        private protected DataField(Type clrType, NpgsqlDbType databaseType)
        {
            ClrType = clrType;
            DatabaseType = databaseType;
        }

        public Type ClrType { get; }
        internal NpgsqlDbType DatabaseType { get; }
        public bool Required { get; init; } = true;
        public int? MaximumLength { get; init; }

        public void Validate(object? value, string name, bool? required = null, int? maximumLength = null)
        {
            if (value is null)
            {
                if (required ?? Required)
                {
                    throw new DataValidationException(name, "required");
                }

                return;
            }

            if (!ClrType.IsInstanceOfType(value))
            {
                throw new DataValidationException(name, "invalid_type");
            }

            ValidateValue(value, name, maximumLength ?? MaximumLength);
        }

        protected abstract void ValidateValue(object value, string name, int? maximumLength);

        public virtual object? Normalize(object? value)
        {
            return value;
        }
    }

    // An uploaded file: its size and the extensions it may have.
    public sealed class FileField : DataField
    {
        public FileField() : base(typeof(UploadedFile), NpgsqlDbType.Unknown)
        {
        }

        public long MaximumBytes { get; init; }
        public IReadOnlyList<string> Extensions { get; init; } = [];

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var file = (UploadedFile)value;
            if (file.Length <= 0 || file.Length > MaximumBytes)
            {
                throw new DataValidationException(name, "out_of_range");
            }

            if (!Extensions.Contains(Path.GetExtension(file.FileName).ToLowerInvariant(), StringComparer.Ordinal))
            {
                throw new DataValidationException(name, "invalid_file_type");
            }
        }
    }

    public sealed class LongField : DataField
    {
        public LongField() : base(typeof(long), NpgsqlDbType.Bigint)
        {
        }

        public long Minimum { get; init; } = long.MinValue;
        public long Maximum { get; init; } = long.MaxValue;

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var number = (long)value;

            if (number < Minimum || number > Maximum)
            {
                throw new DataValidationException(name, "out_of_range");
            }
        }
    }

    public sealed class GuidField : DataField
    {
        public GuidField() : base(typeof(Guid), NpgsqlDbType.Uuid)
        {
        }

        public bool AllowEmpty { get; init; }

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            // Empty is a value, not a missing nullable Guid.
            if (!AllowEmpty && (Guid)value == Guid.Empty)
            {
                throw new DataValidationException(name, "invalid_uuid");
            }
        }
    }

    public sealed class IntField : DataField
    {
        public IntField() : base(typeof(int), NpgsqlDbType.Integer)
        {
        }

        public int Minimum { get; init; } = int.MinValue;
        public int Maximum { get; init; } = int.MaxValue;

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var number = (int)value;

            if (number < Minimum || number > Maximum)
            {
                throw new DataValidationException(name, "out_of_range");
            }
        }
    }

    public sealed class DecimalField : DataField
    {
        public DecimalField() : base(typeof(decimal), NpgsqlDbType.Numeric)
        {
        }

        public decimal Minimum { get; init; } = decimal.MinValue;
        public decimal Maximum { get; init; } = decimal.MaxValue;
        public int Scale { get; init; } = 28;

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var number = (decimal)value;

            if (number < Minimum || number > Maximum || decimal.Round(number, Scale) != number)
            {
                throw new DataValidationException(name, "out_of_range");
            }
        }
    }

    public sealed class StringField : DataField
    {
        private static readonly Regex MacAddressPattern = new("^([0-9A-F]{2}:){5}[0-9A-F]{2}$", RegexOptions.CultureInvariant);
        private static readonly FrozenDictionary<string, string> LocaleNames = CultureInfo.GetCultures(CultureTypes.AllCultures)
            .Where(culture => culture.Name.Length > 0).GroupBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);

        public StringField() : this(NpgsqlDbType.Text)
        {
        }

        internal StringField(NpgsqlDbType databaseType)
            : base(typeof(string), databaseType)
        {
            if (databaseType is not (NpgsqlDbType.Text or NpgsqlDbType.Varchar or NpgsqlDbType.Citext))
            {
                throw new ArgumentOutOfRangeException(nameof(databaseType));
            }
        }

        public bool AllowBlank { get; init; } = true;
        public bool Trim { get; init; }
        public int MinimumLength { get; init; }
        public StringFormat Format { get; init; }

        public override object? Normalize(object? value)
        {
            if (value is string text)
            {
                if (Trim) text = text.Trim();
                return Format == StringFormat.Locale && LocaleNames.TryGetValue(text, out var locale) ? locale : text;
            }

            return value;
        }

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var text = (string)value;

            if (!AllowBlank && string.IsNullOrWhiteSpace(text))
            {
                throw new DataValidationException(name, "required");
            }

            // PostgreSQL lengths count Unicode characters, not UTF-16 units.
            var remaining = text.AsSpan();
            var length = 0;

            while (!remaining.IsEmpty)
            {
                var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);

                if (status != OperationStatus.Done || rune.Value == 0)
                {
                    throw new DataValidationException(name, "invalid_text");
                }

                length++;
                remaining = remaining[consumed..];
            }

            if (length < MinimumLength)
            {
                throw new DataValidationException(name, "too_short");
            }

            if (Format == StringFormat.Locale && !LocaleNames.ContainsKey(text))
            {
                throw new DataValidationException(name, "invalid_locale");
            }

            // Restrict protocol input.
            if (Format == StringFormat.HostName && Uri.CheckHostName(text) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            {
                throw new DataValidationException(name, "invalid_host");
            }

            if (Format == StringFormat.AbsolutePath && (!text.StartsWith('/') || text.StartsWith("//")
                || text.Any(character => char.IsControl(character) || character is '\\' or ',')
                || text.Split('/').Any(segment => segment is "." or "..")))
            {
                throw new DataValidationException(name, "invalid_path");
            }

            if (Format == StringFormat.SingleLine && text.Any(char.IsControl))
            {
                throw new DataValidationException(name, "invalid_text");
            }

            if (Format is StringFormat.HttpUrl or StringFormat.ServiceUrl && (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.UserInfo.Length > 0 || url.Fragment.Length > 0
                || url.Scheme is not ("http" or "https") && (Format == StringFormat.HttpUrl || url.Scheme is not ("nntp" or "nntps"))))
            {
                throw new DataValidationException(name, "invalid_url");
            }

            if (Format == StringFormat.MacAddress && !MacAddressPattern.IsMatch(text))
            {
                throw new DataValidationException(name, "invalid_mac_address");
            }

            if (Format == StringFormat.IPv4Address && (!IPAddress.TryParse(text, out var ipAddress) || ipAddress.AddressFamily != AddressFamily.InterNetwork || ipAddress.ToString() != text))
            {
                throw new DataValidationException(name, "invalid_host");
            }

            // Relative paths stay below their root.
            if (Format == StringFormat.RelativePath && text.Length > 0 && (text.StartsWith('/') || text.EndsWith('/')
                || text.Any(character => char.IsControl(character) || character == '\\')
                || text.Split('/').Any(segment => segment is "" or "." or "..")))
            {
                throw new DataValidationException(name, "invalid_path");
            }

            if (maximumLength is { } limit && length > limit)
            {
                throw new DataValidationException(name, "too_long");
            }

            if (Format == StringFormat.Email
                && (!MailAddress.TryCreate(text, out var address) || address.Address != text
                    || !address.Host.Contains('.') || text.Any(char.IsWhiteSpace)))
            {
                throw new DataValidationException(name, "invalid_email");
            }
        }
    }

    public sealed class BoolField : DataField
    {
        public BoolField() : base(typeof(bool), NpgsqlDbType.Boolean)
        {
        }

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
        }
    }

    public sealed class DateTimeField : DataField
    {
        public DateTimeField() : base(typeof(DateTime), NpgsqlDbType.TimestampTz)
        {
        }

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            if (((DateTime)value).Kind != DateTimeKind.Utc)
            {
                throw new DataValidationException(name, "utc_required");
            }
        }
    }

    // A calendar day without time or zone.
    public sealed class DateField : DataField
    {
        public DateField() : base(typeof(DateOnly), NpgsqlDbType.Date)
        {
        }

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
        }
    }

    public sealed class ByteArrayField : DataField
    {
        public ByteArrayField() : base(typeof(byte[]), NpgsqlDbType.Bytea)
        {
        }

        public int MinimumLength { get; init; }

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var length = ((byte[])value).Length;

            if (length < MinimumLength)
            {
                throw new DataValidationException(name, "too_short");
            }

            if (maximumLength is { } limit && length > limit)
            {
                throw new DataValidationException(name, "too_long");
            }
        }
    }

    public sealed class EnumField : DataField
    {
        public EnumField(Type enumType) : base(enumType, NpgsqlDbType.Smallint)
        {
            if (!enumType.IsEnum || Enum.GetUnderlyingType(enumType) != typeof(byte))
            {
                throw new ArgumentException("Catalog enums must be byte-backed.", nameof(enumType));
            }
        }

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            if (!Enum.IsDefined(ClrType, value))
            {
                throw new DataValidationException(name, "unknown_catalog_value");
            }
        }
    }

    public sealed class IntArrayField : DataField
    {
        public IntArrayField() : base(typeof(int[]), NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
        }

        public int Minimum { get; init; } = int.MinValue;
        public int Maximum { get; init; } = int.MaxValue;

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var values = (int[])value;
            if (maximumLength is { } limit && values.Length > limit || values.Any(item => item < Minimum || item > Maximum))
            {
                throw new DataValidationException(name, "out_of_range");
            }
        }
    }

    public sealed class LongArrayField : DataField
    {
        public LongArrayField() : base(typeof(long[]), NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
        }

        public long Minimum { get; init; } = long.MinValue;

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var values = (long[])value;
            if (maximumLength is { } limit && values.Length > limit || values.Any(item => item < Minimum))
            {
                throw new DataValidationException(name, "out_of_range");
            }
        }
    }

    public sealed class StringArrayField : DataField
    {
        private readonly StringField _element = new();

        public StringArrayField() : base(typeof(string[]), NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
        }

        // Longest single entry; every entry is also trimmed, required and not blank.
        public required int ItemMaximumLength
        {
            get => _element.MaximumLength ?? 0;
            init => _element = new() { MaximumLength = value, AllowBlank = false, Trim = true };
        }

        // "de, en" from one text field becomes ["de", "en"].
        public bool SplitsCommas { get; init; }

        public override object? Normalize(object? value) => value is not string[] values ? value
            : SplitsCommas ? values.SelectMany<string?, string?>(item => item is null ? new string?[] { null } : item.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray()
            : values.Select(item => (string?)_element.Normalize(item)).ToArray();

        protected override void ValidateValue(object value, string name, int? maximumLength)
        {
            var values = (string[])value;
            if (maximumLength is { } limit && values.Length > limit)
            {
                throw new DataValidationException(name, "out_of_range");
            }

            foreach (var item in values)
            {
                _element.Validate(item, name);
            }
        }
    }
}
