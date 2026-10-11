using System.Reflection;
using NpgsqlTypes;

namespace Jularr.Data.Common;

public abstract record DataField(string Table, string Column, Type ClrType, NpgsqlDbType DatabaseType);

public sealed record DataField<T>(string Table, string Column, NpgsqlDbType DatabaseType)
    : DataField(Table, Column, typeof(T), DatabaseType);

[AttributeUsage(AttributeTargets.Property)]
public sealed class DataFieldAttribute : Attribute
{
    public DataField Field { get; }
    public bool Required { get; }

    public DataFieldAttribute(Type fieldsType, string fieldName) : this(fieldsType, fieldName, true)
    {
    }

    public DataFieldAttribute(Type fieldsType, string fieldName, bool required)
    {
        Field = fieldsType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DataField
            ?? throw new ArgumentException("A public static data field is required.", nameof(fieldName));
        Required = required;
    }
}
