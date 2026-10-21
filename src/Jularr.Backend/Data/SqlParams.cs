using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Jularr.Backend.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Backend.Data
{
    internal sealed class SqlParams
    {
        private static readonly ConcurrentDictionary<Type, PropertyBinding[]> Bindings = new();

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

        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Name, NpgsqlDbType Type, object Value)> _entries = [];

        private SqlParams()
        {
        }

        public static SqlParams Create() => new();

        public SqlParams Add(object data)
        {
            ArgumentNullException.ThrowIfNull(data);

            // Reuse compiled field accessors.
            foreach (var binding in Bindings.GetOrAdd(data.GetType(), CreateBindings))
            {
                var value = binding.Read(data);
                binding.Field?.Validate(value, binding.Name);
                AddValue(binding.Name, binding.ClrType, value, binding.DatabaseType);
            }

            return this;
        }

        public SqlParams Add<T>(string name, T value) =>
            AddValue(name, typeof(T), value, null);

        public NpgsqlParameter[] ToArray() =>
        [
            .. _entries.Select(entry => new NpgsqlParameter(entry.Name, entry.Type)
            {
                Value = entry.Value
            })
        ];

        private static PropertyBinding[] CreateBindings(Type type)
        {
            var bindings = new List<PropertyBinding>();

            // Compile metadata once per type.
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                ValidateName(property.Name);
                var field = property.GetCustomAttribute<DataFieldAttribute>();
                var clrType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (field is not null && clrType != field.Field.ClrType)
                {
                    throw new ArgumentException("The parameter type conflicts with its data field.");
                }

                var databaseType = field?.Field.DatabaseType ?? ResolveType(clrType);
                var source = Expression.Parameter(typeof(object), "source");
                var value = Expression.Property(Expression.Convert(source, type), property);
                var read = Expression.Lambda<Func<object, object?>>(Expression.Convert(value, typeof(object)), source).Compile();
                bindings.Add(new PropertyBinding(property.Name, property.PropertyType, databaseType, field, read));
            }

            return bindings.ToArray();
        }

        private SqlParams AddValue(string name, Type declaredType, object? value, NpgsqlDbType? databaseType)
        {
            ValidateName(name);

            if (_names.Contains(name))
            {
                throw new ArgumentException("SQL parameter was already added.", nameof(name));
            }

            var actualType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            var resolvedType = databaseType ?? ResolveType(actualType);

            if (databaseType is { } explicitType)
            {
                if (explicitType != ResolveType(actualType)
                    && !(actualType == typeof(string) && explicitType is NpgsqlDbType.Jsonb or NpgsqlDbType.Citext or NpgsqlDbType.Varchar))
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
            else if (actualType.IsArray && actualType.GetElementType()!.IsEnum)
            {
                // Persisted byte-backed enums travel as smallint values.
                value = value is Array values ? values.Cast<object>().Select(Convert.ToInt16).ToArray() : null;
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

            _names.Add(name);
            _entries.Add((name, resolvedType, value ?? DBNull.Value));

            return this;
        }

        private static NpgsqlDbType ResolveType(Type type)
        {
            if (type.IsArray && type != typeof(byte[]))
            {
                var elementType = type.GetElementType()!;
                if (type.GetArrayRank() != 1 || (elementType != typeof(string) && elementType != typeof(int)
                    && elementType != typeof(long) && elementType != typeof(bool) && elementType != typeof(Guid)
                    && !(elementType.IsEnum && Enum.GetUnderlyingType(elementType) == typeof(byte))))
                {
                    throw new NotSupportedException("The PostgreSQL array element type is unsupported.");
                }

                return NpgsqlDbType.Array | ResolveType(elementType);
            }

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

        private sealed record PropertyBinding(string Name, Type ClrType, NpgsqlDbType DatabaseType, DataFieldAttribute? Field, Func<object, object?> Read);
    }
}
