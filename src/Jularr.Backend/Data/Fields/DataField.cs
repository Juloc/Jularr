using System.Buffers;
using System.Text;
using Jularr.Data.Common;
using NpgsqlTypes;

namespace Jularr.Data.Fields;

public abstract class DataField
{
    protected DataField(Type clrType, NpgsqlDbType databaseType)
    {
        ClrType = clrType;
        DatabaseType = databaseType;
    }

    public Type ClrType { get; }
    public NpgsqlDbType DatabaseType { get; }
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

public sealed class StringField : DataField
{
    public StringField(NpgsqlDbType databaseType = NpgsqlDbType.Text)
        : base(typeof(string), databaseType)
    {
        if (databaseType is not (NpgsqlDbType.Text or NpgsqlDbType.Varchar or NpgsqlDbType.Citext))
        {
            throw new ArgumentOutOfRangeException(nameof(databaseType));
        }
    }

    public bool AllowBlank { get; init; } = true;
    public bool Trim { get; init; }

    public override object? Normalize(object? value)
    {
        if (Trim && value is string text)
        {
            return text.Trim();
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

        if (maximumLength is { } limit && length > limit)
        {
            throw new DataValidationException(name, "too_long");
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

public sealed class StringArrayField : DataField
{
    private static readonly StringField Element = new();

    public StringArrayField() : base(typeof(string[]), NpgsqlDbType.Array | NpgsqlDbType.Text)
    {
    }

    protected override void ValidateValue(object value, string name, int? maximumLength)
    {
        foreach (var item in (string[])value)
        {
            Element.Validate(item, name);
        }
    }
}
