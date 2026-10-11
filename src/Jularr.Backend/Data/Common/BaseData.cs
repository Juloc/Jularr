using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jularr.Data.Common;

public sealed class DataValidationException : ArgumentException
{
    public DataValidationException(string field, string code)
        : base("Data field validation failed.", field)
    {
        Field = field;
        Code = code;
    }

    public string Field { get; }
    public string Code { get; }
}

public abstract class BaseData
{
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Property, DataFieldAttribute Field)[]> Fields = new();
    private HashSet<string>? _provided;

    protected BaseData(bool trackChanges = false)
    {
        _provided = trackChanges ? new(StringComparer.Ordinal) : null;
    }

    internal BaseData Copy()
    {
        var copy = (BaseData)MemberwiseClone();
        copy._provided = _provided is null ? null : new HashSet<string>(_provided, StringComparer.Ordinal);
        return copy;
    }

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        field = value;
        _provided?.Add(name);
    }

    internal bool IsProvided(string name) => _provided is null || _provided.Contains(name);

    internal static (PropertyInfo Property, DataFieldAttribute Field)[] GetFields(Type type)
    {
        return Fields.GetOrAdd(type, dataType =>
        {
            var fields = new List<(PropertyInfo, DataFieldAttribute)>();

            foreach (var property in dataType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                var field = property.GetCustomAttribute<DataFieldAttribute>();

                if (field is not null)
                {
                    fields.Add((property, field));
                }
            }

            return fields.ToArray();
        });
    }

    public virtual void SetData(params BaseData[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var targets = GetFields(GetType()).ToDictionary(field => field.Property.Name, StringComparer.Ordinal);
        var changes = new Dictionary<PropertyInfo, object?>();

        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            source.Validate();

            foreach (var (input, _) in GetFields(source.GetType()))
            {
                // Only equally named properties are copied; aliases are assigned by the caller.
                if (!source.IsProvided(input.Name) || !targets.TryGetValue(input.Name, out var target))
                {
                    continue;
                }

                var setter = target.Property.SetMethod;

                if (setter is null || setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
                {
                    continue;
                }

                var value = target.Field.Field.Normalize(input.GetValue(source));
                target.Field.Validate(value, target.Property.Name);
                changes[target.Property] = value;
            }
        }

        // Validate all changed values before touching the destination.
        foreach (var (property, value) in changes)
        {
            property.SetValue(this, value);
        }

        Validate();
    }

    public virtual void Validate()
    {
        var fields = GetFields(GetType());

        foreach (var (property, field) in fields)
        {
            if ((Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) != field.Field.ClrType)
            {
                throw new InvalidOperationException("A data property conflicts with its database field type.");
            }

            field.Validate(property.GetValue(this), property.Name);
        }
    }
}

public abstract class BaseDataList : BaseData
{
    internal abstract IReadOnlyList<BaseData> Items { get; }

    public override void SetData(params BaseData[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Validate();

        if (Items.Count == 0)
        {
            if (sources.Any(source => source is not BaseDataList list || list.Items.Count != 0))
            {
                throw new DataValidationException("Items", "unknown_id");
            }

            return;
        }

        var key = GetFields(Items[0].GetType()).SingleOrDefault(item => item.Property.Name == "Id");
        if (key.Property is null)
        {
            throw new InvalidOperationException("A data list update requires a declared Id field.");
        }

        // Stage every row before applying any list changes.
        var staged = new Dictionary<object, (BaseData Original, BaseData Copy)>();

        foreach (var item in Items)
        {
            var id = key.Property.GetValue(item) ?? throw new DataValidationException("Id", "required");
            if (!staged.TryAdd(id, (item, item.Copy())))
            {
                throw new DataValidationException("Id", "duplicate_id");
            }
        }

        foreach (var source in sources)
        {
            if (source is not BaseDataList list)
            {
                throw new ArgumentException("Data lists can only merge data lists.", nameof(sources));
            }

            list.Validate();
            var seen = new HashSet<object>();

            foreach (var item in list.Items)
            {
                var sourceKey = GetFields(item.GetType()).SingleOrDefault(field =>
                    field.Property.Name == "Id");
                var id = sourceKey.Property?.GetValue(item) ?? throw new DataValidationException("Id", "required");
                if (!seen.Add(id) || !staged.TryGetValue(id, out var target))
                {
                    throw new DataValidationException("Id", "unknown_or_duplicate_id");
                }

                target.Copy.SetData(item);
            }
        }

        foreach (var (original, copy) in staged.Values)
        {
            original.SetData(copy);
        }
    }
}

[JsonConverter(typeof(BaseDataListJsonConverterFactory))]
public sealed class BaseDataList<T> : BaseDataList, IReadOnlyList<T>, IServiceOutput where T : BaseData
{
    private readonly T[] _items;

    public BaseDataList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    public int Count => _items.Length;
    public T this[int index] => _items[index];
    internal override IReadOnlyList<BaseData> Items => _items;

    public override void Validate()
    {
        foreach (var item in _items)
        {
            if (item is null)
            {
                throw new DataValidationException("Items", "required");
            }

            item.Validate();
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class BaseDataListJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(BaseDataList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ListConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;

    private sealed class ListConverter<T> : JsonConverter<BaseDataList<T>> where T : BaseData
    {
        public override BaseDataList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? throw new JsonException("A data list must be an array."));

        public override void Write(Utf8JsonWriter writer, BaseDataList<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                JsonSerializer.Serialize(writer, item, options);
            }

            writer.WriteEndArray();
        }
    }
}

public sealed class PartialDataJsonConverter<T> : JsonConverter<T> where T : BaseData, new()
{
    private static readonly ConcurrentDictionary<(JsonNamingPolicy? Policy, bool IgnoreCase), Dictionary<string, PropertyInfo>> Properties = new();

    private static Dictionary<string, PropertyInfo> GetProperties(JsonSerializerOptions options)
    {
        return Properties.GetOrAdd((options.PropertyNamingPolicy, options.PropertyNameCaseInsensitive), static key =>
        {
            var comparer = key.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var properties = new Dictionary<string, PropertyInfo>(comparer);

            foreach (var (property, _) in BaseData.GetFields(typeof(T)))
            {
                if (property.SetMethod?.IsPublic != true)
                {
                    continue;
                }

                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                    ?? key.Policy?.ConvertName(property.Name) ?? property.Name;
                properties.Add(name, property);
            }

            return properties;
        });
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Partial data must be an object.");
        }

        var data = new T();
        var properties = GetProperties(options);
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (properties.TryGetValue(field.Name, out var property))
            {
                property.SetValue(data, field.Value.Deserialize(property.PropertyType, options));
            }
            else if (options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
            {
                throw new JsonException("Unknown partial data field.");
            }
        }

        return data;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, property) in GetProperties(options))
        {
            if (value.IsProvided(property.Name))
            {
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, property.GetValue(value), property.PropertyType, options);
            }
        }

        writer.WriteEndObject();
    }
}
