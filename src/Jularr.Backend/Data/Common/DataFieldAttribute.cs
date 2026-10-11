using System.Reflection;
using Jularr.Data.Fields;

namespace Jularr.Data.Common;

[AttributeUsage(AttributeTargets.Property)]
public sealed class DataFieldAttribute : Attribute
{
    public DataField Field { get; }
    public bool Required { get; }
    public int? MaximumLength { get; }

    public DataFieldAttribute(Type fieldsType, string fieldName)
    {
        Field = Resolve(fieldsType, fieldName);
        Required = Field.Required;
    }

    public DataFieldAttribute(Type fieldsType, string fieldName, bool required) : this(fieldsType, fieldName)
    {
        Required = required;
    }

    public DataFieldAttribute(Type fieldsType, string fieldName, bool required, int maximumLength)
        : this(fieldsType, fieldName, required)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        if (Field.MaximumLength is { } limit && maximumLength > limit)
        {
            throw new ArgumentException("A data contract cannot exceed its database field length.", nameof(maximumLength));
        }

        MaximumLength = maximumLength;
    }

    public void Validate(object? value, string name) => Field.Validate(value, name, Required, MaximumLength);

    private static DataField Resolve(Type fieldsType, string fieldName) =>
        fieldsType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DataField
        ?? throw new ArgumentException("A public static data field is required.", nameof(fieldName));
}
