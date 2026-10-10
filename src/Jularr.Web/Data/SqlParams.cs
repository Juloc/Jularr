using System.Collections.Concurrent;
using System.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Web.Data;

public sealed class SqlParams
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo> Properties = new();

    private static readonly IReadOnlyDictionary<Type, NpgsqlDbType> DefaultTypes =
        new Dictionary<Type, NpgsqlDbType>
        {
            [typeof(long)] = NpgsqlDbType.Bigint,
            [typeof(int)] = NpgsqlDbType.Integer,
            [typeof(short)] = NpgsqlDbType.Smallint,
            [typeof(byte)] = NpgsqlDbType.Smallint,
            [typeof(string)] = NpgsqlDbType.Text,
            [typeof(bool)] = NpgsqlDbType.Boolean,
            [typeof(Guid)] = NpgsqlDbType.Uuid,
            [typeof(decimal)] = NpgsqlDbType.Numeric,
            [typeof(float)] = NpgsqlDbType.Real,
            [typeof(double)] = NpgsqlDbType.Double,
            [typeof(DateTime)] = NpgsqlDbType.TimestampTz,
            [typeof(DateTimeOffset)] = NpgsqlDbType.TimestampTz,
            [typeof(DateOnly)] = NpgsqlDbType.Date,
            [typeof(TimeOnly)] = NpgsqlDbType.Time,
            [typeof(byte[])] = NpgsqlDbType.Bytea
        };

    private readonly object? source;
    private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, NpgsqlDbType Type, object Value)> entries = [];

    private SqlParams(object? source)
    {
        this.source = source;
    }

    public static SqlParams From<T>(T data) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(data);
        return new SqlParams(data);
    }

    public static SqlParams Create() => new(null);

    public SqlParams Add(string name) => AddProperty(name, null);

    public SqlParams Add(string name, NpgsqlDbType type) => AddProperty(name, type);

    public SqlParams Add<T>(string name, T value) =>
        AddValue(name, typeof(T), value, null);

    public SqlParams Add<T>(string name, T value, NpgsqlDbType type) =>
        AddValue(name, typeof(T), value, type);

    public NpgsqlParameter[] ToArray() =>
    [
        .. entries.Select(entry => new NpgsqlParameter(entry.Name, entry.Type)
        {
            Value = entry.Value
        })
    ];

    private SqlParams AddProperty(string name, NpgsqlDbType? databaseType)
    {
        if (source is null)
        {
            throw new InvalidOperationException("A transport object is required for property parameters.");
        }

        ValidateName(name);

        var property = Properties.GetOrAdd((source.GetType(), name), static key =>
        {
            var result = key.Type.GetProperty(key.Name, BindingFlags.Public | BindingFlags.Instance);

            if (result?.GetMethod is null || !result.GetMethod.IsPublic
                || result.GetIndexParameters().Length != 0)
            {
                throw new ArgumentException("Unknown or unreadable transport property.", "name");
            }

            return result;
        });

        return AddValue(name, property.PropertyType, property.GetValue(source), databaseType);
    }

    private SqlParams AddValue(string name, Type declaredType, object? value, NpgsqlDbType? databaseType)
    {
        ValidateName(name);

        if (names.Contains(name))
        {
            throw new ArgumentException("SQL parameter was already added.", nameof(name));
        }

        var actualType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        var resolvedType = databaseType ?? ResolveType(actualType);

        if (databaseType is { } explicitType)
        {
            bool isCompatible;

            if (actualType.IsArray && actualType != typeof(byte[]))
            {
                var elementType = actualType.GetElementType()!;
                isCompatible = actualType.GetArrayRank() == 1
                    && (elementType == typeof(string)
                        || elementType == typeof(int)
                        || elementType == typeof(long)
                        || elementType == typeof(bool)
                        || elementType == typeof(Guid))
                    && explicitType == (NpgsqlDbType.Array | ResolveType(elementType));
            }
            else
            {
                isCompatible = explicitType == ResolveType(actualType)
                    || (actualType == typeof(string)
                        && explicitType is NpgsqlDbType.Jsonb or NpgsqlDbType.Citext);
            }

            if (!isCompatible)
            {
                throw new ArgumentException(
                    "The PostgreSQL type is incompatible with the declared CLR type.",
                    nameof(databaseType));
            }
        }

        if (actualType.IsEnum)
        {
            if (Enum.GetUnderlyingType(actualType) != typeof(byte))
            {
                throw new NotSupportedException("Only persisted byte-backed enums are supported.");
            }

            if (resolvedType != NpgsqlDbType.Smallint)
            {
                throw new ArgumentException("Persisted byte-backed enums require PostgreSQL smallint.", nameof(databaseType));
            }

            value = value is null ? null : Convert.ToInt16(value);
        }
        else if (actualType == typeof(byte))
        {
            if (resolvedType == NpgsqlDbType.Smallint && value is not null)
            {
                value = Convert.ToInt16(value);
            }
        }

        if (value is DateTime dateTime && resolvedType == NpgsqlDbType.TimestampTz
            && dateTime.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("PostgreSQL timestamptz requires a UTC DateTime.", nameof(value));
        }

        if (value is DateTimeOffset dateTimeOffset && resolvedType == NpgsqlDbType.TimestampTz
            && dateTimeOffset.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("PostgreSQL timestamptz requires a UTC DateTimeOffset.", nameof(value));
        }

        names.Add(name);
        entries.Add((name, resolvedType, value ?? DBNull.Value));

        return this;
    }

    private static NpgsqlDbType ResolveType(Type type)
    {
        if (type.IsEnum)
        {
            if (Enum.GetUnderlyingType(type) == typeof(byte))
            {
                return NpgsqlDbType.Smallint;
            }

            throw new NotSupportedException("Only persisted byte-backed enums are supported.");
        }

        if (DefaultTypes.TryGetValue(type, out var resolved))
        {
            return resolved;
        }

        throw new NotSupportedException($"No PostgreSQL mapping is defined for {type.Name}.");
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            var valid = IsLetter(character) || character == '_'
                || (i > 0 && char.IsAsciiDigit(character));

            if (!valid)
            {
                throw new ArgumentException("SQL parameter names must be simple ASCII identifiers.", nameof(name));
            }
        }
    }

    private static bool IsLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
