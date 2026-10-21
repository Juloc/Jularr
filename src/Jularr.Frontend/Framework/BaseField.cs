using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Field;
using Jularr.Backend.Data.Fields;

namespace Jularr.Frontend.Framework
{
    public enum FieldControl
    {
        Text,
        Password,
        TextArea,
        Number,
        Range,
        Checkbox,
        Select,
        MultiSelect,
        File
    }

    public sealed record BaseField(
        string Name,
        Type ValueType,
        FieldControl Control,
        bool Required,
        int? MaximumLength,
        decimal? Minimum,
        decimal? Maximum,
        string LabelKey)
    {
        private static readonly ConcurrentDictionary<PropertyInfo, (DataFieldAttribute Field, Func<BaseData, object?> Read)> Properties = new();

        public int? MinimumLength { get; init; }
        public int? Scale { get; init; }

        // File types a file field accepts.
        public string? Accept { get; init; }

        // An address is typed with the address keyboard, without spelling or capitals.
        public bool IsAddress { get; init; }
        public bool IsUsername { get; init; }

        // The example shown in the empty field and the explanation behind the label's (i): the field's own unless a form names others.
        public string PlaceholderKey { get; init; } = "";
        public string InfoKey { get; init; } = "";
        internal Func<BaseData, object?> ReadValue { get; init; } = _ => throw new InvalidOperationException("Field access requires property metadata.");

        public static BaseField From<TData, TValue>(Expression<Func<TData, TValue>> selector, FieldControl control, string? labelKey = null)
            where TData : BaseData
        {
            var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } cast
                ? cast.Operand
                : selector.Body;

            if (body is not MemberExpression { Member: PropertyInfo property }
                || property.DeclaringType is null
                || !property.DeclaringType.IsAssignableFrom(typeof(TData))
                || ((MemberExpression)body).Expression != selector.Parameters[0])
            {
                throw new ArgumentException("Fields must select a data property.", nameof(selector));
            }

            // Cache metadata and property access.
            var cached = Properties.GetOrAdd(property, member =>
            {
                var metadata = member.GetCustomAttribute<DataFieldAttribute>()
                    ?? throw new InvalidOperationException("Editable fields require backend DataField metadata.");
                var data = Expression.Parameter(typeof(BaseData), "data");
                var value = Expression.Property(Expression.Convert(data, member.DeclaringType!), member);
                var getter = Expression.Lambda<Func<BaseData, object?>>(Expression.Convert(value, typeof(object)), data).Compile();
                return (metadata, getter);
            });
            var field = cached.Field;
            var (minimum, maximum) = field.Field switch
            {
                LongField number => (number.Minimum, number.Maximum),
                IntField number => (number.Minimum, number.Maximum),
                DecimalField number => (number.Minimum, number.Maximum),
                _ => ((decimal?)null, (decimal?)null)
            };

            return new BaseField(
                property.Name,
                property.PropertyType,
                control,
                field.Required,
                field.MaximumLength ?? field.Field.MaximumLength,
                minimum,
                maximum,
                labelKey ?? $"field.{typeof(TData).Name}.{property.Name}")
            {
                ReadValue = cached.Read,
                MinimumLength = field.Field is StringField text && text.MinimumLength > 0 ? text.MinimumLength : null,
                Scale = field.Field is DecimalField fraction ? fraction.Scale : null,
                Accept = field.Field is FileField file ? string.Join(',', file.Extensions) : null,
                IsAddress = field.Field is StringField { Format: StringFormat.HttpUrl or StringFormat.ServiceUrl },
                IsUsername = field.Field is StringField { Format: StringFormat.Username },
                PlaceholderKey = "hint." + field.Key + ".placeholder",
                InfoKey = "hint." + field.Key + ".info"
            };
        }
    }
}
