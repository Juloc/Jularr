using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields;

namespace Jularr.Frontend.Framework
{
    public enum FieldControl
    {
        Text,
        Password,
        TextArea,
        Number,
        Checkbox,
        Select,
        MultiSelect
    }

    public sealed record BaseField(
        string Name,
        Type ValueType,
        FieldControl Control,
        bool Required,
        int? MaximumLength,
        long? Minimum,
        long? Maximum,
        string LabelKey)
    {
        private static readonly ConcurrentDictionary<PropertyInfo, (DataFieldAttribute Field, Func<BaseData, object?> Read)> Properties = new();

        public int? MinimumLength { get; init; }
        public int? Scale { get; init; }
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
                DecimalField number => ((long)decimal.Ceiling(number.Minimum), (long)decimal.Floor(number.Maximum)),
                _ => ((long?)null, (long?)null)
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
                Scale = field.Field is DecimalField fraction ? fraction.Scale : null
            };
        }
    }
}
