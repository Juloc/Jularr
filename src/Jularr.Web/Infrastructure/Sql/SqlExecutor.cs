using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Text.Json;
using Jularr.Web.Data;
using Npgsql;
using NpgsqlTypes;

namespace Jularr.Infrastructure.Sql;

internal static class SqlExecutor
{
    private static readonly ConcurrentDictionary<(string Sql, Type Parameters, Type? Data), Binding[]> Bindings = new();
    private static readonly ConcurrentDictionary<(Type Type, string Columns), RowMap> RowMaps = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static NpgsqlParameter[] Bind(string sql, object parameters, object? data, long? actorAccountId, long? activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var bindings = Bindings.GetOrAdd((sql, parameters.GetType(), data?.GetType()), static key => CompileBindings(key.Sql, key.Parameters, key.Data));
        var pageRequest = bindings.Any(binding => binding.Source == BindingSource.Page) ? GetPageRequest(parameters) : null;
        var values = new NpgsqlParameter[bindings.Length];

        for (var index = 0; index < bindings.Length; index++)
        {
            var binding = bindings[index];

            if (binding.Source == BindingSource.Actor)
            {
                values[index] = SqlParams.Create().Add(binding.Name, actorAccountId ?? throw new InvalidOperationException("No verified ActorAccountId is available.")).ToArray()[0];
            }
            else if (binding.Source == BindingSource.Profile)
            {
                values[index] = SqlParams.Create().Add(binding.Name, activeProfileId ?? throw new InvalidOperationException("No verified ActiveProfileId is available.")).ToArray()[0];
            }
            else if (binding.Source == BindingSource.Page)
            {
                var isPageSize = string.Equals(binding.Name, "PageSize", StringComparison.OrdinalIgnoreCase);
                var number = isPageSize ? pageRequest!.PageSize : pageRequest!.Offset;
                values[index] = isPageSize
                    ? SqlParams.Create().Add(binding.Name, (int)number).ToArray()[0]
                    : SqlParams.Create().Add(binding.Name, number).ToArray()[0];
            }
            else
            {
                var source = binding.Source == BindingSource.Parameters ? parameters : data!;
                var property = source.GetType().GetProperty(binding.Name, BindingFlags.Public | BindingFlags.Instance);
                if (property is null)
                {
                    throw new InvalidOperationException($"SQL parameter '{binding.Name}' is no longer a readable DTO property.");
                }

                // The existing SqlParams implementation supports primitive values and bytea,
                // but not PostgreSQL arrays. Bind only the known typed, one-dimensional arrays.
                // All other values reuse the central SqlParams type validation.
                if (property.PropertyType.IsArray && property.PropertyType != typeof(byte[]))
                {
                    var arrayType = GetArrayDatabaseType(property.PropertyType);
                    if (binding.DatabaseType is { } castType && castType != arrayType)
                    {
                        throw new ArgumentException($"The SQL array cast for '{binding.Name}' does not match its CLR element type.");
                    }

                    values[index] = new NpgsqlParameter(binding.Name, arrayType)
                    {
                        Value = property.GetValue(source) ?? DBNull.Value
                    };
                }
                else
                {
                    var builder = SqlParams.From(source);
                    values[index] = binding.DatabaseType.HasValue
                        ? builder.Add(binding.Name, binding.DatabaseType.Value).ToArray()[0]
                        : builder.Add(binding.Name).ToArray()[0];
                }
            }
        }

        return values;
    }

    private static NpgsqlDbType GetArrayDatabaseType(Type type)
    {
        if (type.GetArrayRank() != 1)
        {
            throw new NotSupportedException("Only one-dimensional PostgreSQL arrays are supported.");
        }

        var element = type.GetElementType();
        var dbElement = element == typeof(long) ? NpgsqlDbType.Bigint
            : element == typeof(int) ? NpgsqlDbType.Integer
            : element == typeof(string) ? NpgsqlDbType.Text
            : element == typeof(bool) ? NpgsqlDbType.Boolean
            : element == typeof(Guid) ? NpgsqlDbType.Uuid
            : throw new NotSupportedException($"No PostgreSQL array mapping exists for {element?.Name}.");

        return NpgsqlDbType.Array | dbElement;
    }

    private static Binding[] CompileBindings(string sql, Type parametersType, Type? dataType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var tokens = ScanParameters(sql);
        var result = new List<Binding>(tokens.Count);

        foreach (var token in tokens)
        {
            var name = token.Name;
            BindingSource source;

            if (name.Equals("ActorAccountId", StringComparison.OrdinalIgnoreCase))
            {
                source = BindingSource.Actor;
            }
            else if (name.Equals("ActiveProfileId", StringComparison.OrdinalIgnoreCase))
            {
                source = BindingSource.Profile;
            }
            else if (name.Equals("Offset", StringComparison.OrdinalIgnoreCase) || name.Equals("PageSize", StringComparison.OrdinalIgnoreCase))
            {
                source = BindingSource.Page;
            }
            else
            {
                var inParameters = parametersType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                var inData = dataType?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (inParameters is not null && inData is not null)
                {
                    throw new InvalidOperationException($"SQL parameter '{name}' exists in both request objects.");
                }

                if (inParameters is null && inData is null)
                {
                    throw new InvalidOperationException($"SQL parameter '{name}' has no corresponding request property.");
                }

                var property = inParameters ?? inData!;
                if (property.GetMethod is null || !property.GetMethod.IsPublic || property.GetIndexParameters().Length > 0)
                {
                    throw new InvalidOperationException($"SQL parameter '{name}' is not a readable property.");
                }

                if (!string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"SQL parameter '{name}' must match its DTO property casing.");
                }

                source = inParameters is not null ? BindingSource.Parameters : BindingSource.Data;
            }

            if (source is BindingSource.Actor or BindingSource.Profile or BindingSource.Page
                && (parametersType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null && source is not BindingSource.Page
                    || dataType?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null))
            {
                throw new InvalidOperationException($"Reserved SQL parameter '{name}' cannot be provided by an untrusted DTO.");
            }

            result.Add(new Binding(name, source, source is BindingSource.Parameters or BindingSource.Data ? token.DatabaseType : null));
        }

        return [.. result];
    }

    internal static PageRequest GetPageRequest(object parameters)
    {
        if (parameters is PageRequest request)
        {
            return request;
        }

        var type = parameters.GetType();
        var page = type.GetProperty("Page", BindingFlags.Instance | BindingFlags.Public)?.GetValue(parameters);
        var size = type.GetProperty("PageSize", BindingFlags.Instance | BindingFlags.Public)?.GetValue(parameters);

        if (page is not int pageNumber || size is not int pageSize)
        {
            throw new InvalidOperationException("Paged SQL requires int Page and PageSize on the parameters DTO.");
        }

        return new PageRequest(pageNumber, pageSize);
    }

    private static IReadOnlyList<ParameterToken> ScanParameters(string sql)
    {
        var tokens = new Dictionary<string, ParameterToken>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < sql.Length;)
        {
            var current = sql[index];

            if (current == '\'')
            {
                var escapeBackslash = index > 0 && sql[index - 1] is 'E' or 'e' && (index == 1 || !IsNameChar(sql[index - 2]));
                index++;
                while (index < sql.Length)
                {
                    if (escapeBackslash && sql[index] == '\\')
                    {
                        index = Math.Min(index + 2, sql.Length);
                    }
                    else if (sql[index] == '\'')
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == '\'')
                        {
                            index += 2;
                        }
                        else
                        {
                            index++;
                            break;
                        }
                    }
                    else
                    {
                        index++;
                    }
                }

                continue;
            }

            if (current == '"')
            {
                index++;
                while (index < sql.Length)
                {
                    if (sql[index] == '"')
                    {
                        if (index + 1 < sql.Length && sql[index + 1] == '"')
                        {
                            index += 2;
                        }
                        else
                        {
                            index++;
                            break;
                        }
                    }
                    else
                    {
                        index++;
                    }
                }

                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var depth = 1;
                index += 2;
                while (index < sql.Length && depth > 0)
                {
                    if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
                    {
                        depth++;
                        index += 2;
                    }
                    else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
                    {
                        depth--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }

                continue;
            }

            if (current == '$')
            {
                var end = index + 1;
                while (end < sql.Length && IsNameChar(sql[end]))
                {
                    end++;
                }

                if (end < sql.Length && sql[end] == '$')
                {
                    var delimiter = sql[index..(end + 1)];
                    var next = sql.IndexOf(delimiter, end + 1, StringComparison.Ordinal);
                    if (next >= 0)
                    {
                        index = next + delimiter.Length;
                        continue;
                    }
                }
            }

            if (current == '@' && index + 1 < sql.Length && IsNameStart(sql[index + 1]) && (index == 0 || sql[index - 1] != '@'))
            {
                var start = ++index;
                while (index < sql.Length && IsNameChar(sql[index]))
                {
                    index++;
                }

                var name = sql[start..index];
                var type = GetCastType(sql, index);

                if (tokens.TryGetValue(name, out var prior))
                {
                    if (prior.DatabaseType != type && prior.DatabaseType.HasValue && type.HasValue)
                    {
                        throw new InvalidOperationException($"SQL parameter '{name}' has conflicting type casts.");
                    }

                    if (!prior.DatabaseType.HasValue && type.HasValue)
                    {
                        tokens[name] = new ParameterToken(prior.Name, type);
                    }
                }
                else
                {
                    tokens.Add(name, new ParameterToken(name, type));
                }

                continue;
            }

            index++;
        }

        return tokens.Values.ToArray();
    }

    private static NpgsqlDbType? GetCastType(string sql, int index)
    {
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
        {
            index++;
        }

        if (index + 1 >= sql.Length || sql[index] != ':' || sql[index + 1] != ':')
        {
            return null;
        }

        index += 2;
        while (index < sql.Length && char.IsWhiteSpace(sql[index]))
        {
            index++;
        }

        var start = index;
        while (index < sql.Length && IsNameChar(sql[index]))
        {
            index++;
        }

        var type = sql[start..index].ToLowerInvariant();
        var result = type switch
        {
            "jsonb" => NpgsqlDbType.Jsonb,
            "citext" => NpgsqlDbType.Citext,
            "text" => NpgsqlDbType.Text,
            "bigint" or "int8" => NpgsqlDbType.Bigint,
            "integer" or "int4" => NpgsqlDbType.Integer,
            "smallint" or "int2" => NpgsqlDbType.Smallint,
            "boolean" or "bool" => NpgsqlDbType.Boolean,
            "uuid" => NpgsqlDbType.Uuid,
            _ => (NpgsqlDbType?)null
        };

        if (result.HasValue && index + 1 < sql.Length && sql[index] == '[' && sql[index + 1] == ']')
        {
            result |= NpgsqlDbType.Array;
        }

        return result;
    }

    private static bool IsNameStart(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    private static bool IsNameChar(char value) => IsNameStart(value) || char.IsAsciiDigit(value);

    internal static T MapRow<T>(NpgsqlDataReader reader)
    {
        var columns = string.Join('\u001f', Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        var map = RowMaps.GetOrAdd((typeof(T), columns), static key => CreateRowMap(key.Type, key.Columns.Split('\u001f')));
        var arguments = new object?[map.Parameters.Length];

        for (var index = 0; index < map.Parameters.Length; index++)
        {
            var value = reader.GetValue(map.Ordinals[index]);
            arguments[index] = ConvertValue(value, map.Parameters[index].ParameterType, reader.GetDataTypeName(map.Ordinals[index]));
        }

        return (T)map.Constructor.Invoke(arguments);
    }

    private static RowMap CreateRowMap(Type type, string[] columns)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        foreach (var constructor in constructors.OrderByDescending(item => item.GetParameters().Length))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != columns.Length)
            {
                continue;
            }

            var ordinals = new int[parameters.Length];
            var success = true;
            for (var index = 0; index < parameters.Length; index++)
            {
                var hits = Enumerable.Range(0, columns.Length).Where(n => string.Equals(columns[n], parameters[index].Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (hits.Length != 1)
                {
                    success = false;
                    break;
                }

                ordinals[index] = hits[0];
            }

            if (success && ordinals.Distinct().Count() == ordinals.Length)
            {
                return new RowMap(constructor, parameters, ordinals);
            }
        }

        throw new InvalidOperationException($"SQL result columns do not match a public constructor on {type.Name}.");
    }

    private static object? ConvertValue(object value, Type targetType, string databaseType)
    {
        if (value is DBNull)
        {
            if (Nullable.GetUnderlyingType(targetType) is not null || !targetType.IsValueType)
            {
                return null;
            }

            throw new InvalidOperationException($"Database NULL cannot map to required {targetType.Name}.");
        }

        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (databaseType is "json" or "jsonb" && type != typeof(string))
        {
            var json = value as string ?? JsonSerializer.Serialize(value);
            return JsonSerializer.Deserialize(json, targetType, JsonOptions)
                ?? throw new InvalidOperationException($"JSON returned null for required {targetType.Name}.");
        }

        if (type.IsEnum)
        {
            var enumValue = Enum.ToObject(type, value);
            if (!Enum.IsDefined(type, enumValue))
            {
                throw new InvalidOperationException($"SQL returned an undefined {type.Name} value.");
            }

            return enumValue;
        }

        if (type.IsInstanceOfType(value))
        {
            return value;
        }

        return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
    }

    private enum BindingSource : byte
    {
        Parameters,
        Data,
        Actor,
        Profile,
        Page
    }

    private sealed record ParameterToken(string Name, NpgsqlDbType? DatabaseType);

    private sealed record Binding(string Name, BindingSource Source, NpgsqlDbType? DatabaseType);

    private sealed record RowMap(ConstructorInfo Constructor, ParameterInfo[] Parameters, int[] Ordinals);
}
