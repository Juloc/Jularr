using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Jularr.Backend.Data;
using Jularr.Backend.Data.Common;
using Npgsql;

namespace Jularr.Backend.Infrastructure.Sql
{
    internal static class SqlExecutor
    {
        private static readonly ConcurrentDictionary<(Type Type, string Columns), RowMap> RowMaps = new();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        internal static NpgsqlParameter[] Bind(SqlStatement statement)
        {
            var parameters = SqlParams.Create();

            // Bind the supplied objects.
            foreach (var source in statement.Parameters)
            {
                parameters.Add(source);
            }

            if (statement.Page is { } page)
            {
                parameters.Add(page);
            }

            return parameters.ToArray();
        }

        internal static T MapRow<T>(NpgsqlDataReader reader)
        {
            var columns = string.Join('\u001f', Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
            var map = RowMaps.GetOrAdd((typeof(T), columns), static key => CreateRowMap(key.Type, key.Columns.Split('\u001f')));
            var arguments = new object?[map.Parameters.Length];

            for (var index = 0; index < map.Parameters.Length; index++)
            {
                var value = reader.GetValue(map.Ordinals[index]);
                if (value is DBNull && map.Required[index])
                {
                    throw new InvalidOperationException("Database NULL cannot map to a required data field.");
                }

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
                    var required = parameters.Select(parameter =>
                        type.GetProperty(parameter.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                            ?.GetCustomAttribute<DataFieldAttribute>()?.Required == true).ToArray();
                    return new RowMap(constructor, parameters, ordinals, required);
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

            // Deserialize structured JSON results.
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

        private sealed record RowMap(ConstructorInfo Constructor, ParameterInfo[] Parameters, int[] Ordinals, bool[] Required);
    }
}
