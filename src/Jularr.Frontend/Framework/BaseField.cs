using System.Linq.Expressions;
using System.Reflection;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields;

namespace Jularr.Frontend.Framework;

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
    public static BaseField From<TData, TValue>(
        Expression<Func<TData, TValue>> selector, FieldControl control, string? labelKey = null)
        where TData : BaseData
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } cast
            ? cast.Operand
            : selector.Body;

        if (body is not MemberExpression { Member: PropertyInfo property }
            || property.DeclaringType is null
            || !property.DeclaringType.IsAssignableFrom(typeof(TData)))
        {
            throw new ArgumentException("Fields must select a data property.", nameof(selector));
        }

        var field = property.GetCustomAttribute<DataFieldAttribute>()
            ?? throw new InvalidOperationException("Editable fields require backend DataField metadata.");
        var minimum = field.Field is LongField number ? number.Minimum : (long?)null;
        var maximum = field.Field is LongField limit ? limit.Maximum : (long?)null;

        return new BaseField(
            property.Name,
            property.PropertyType,
            control,
            field.Required,
            field.MaximumLength ?? field.Field.MaximumLength,
            minimum,
            maximum,
            labelKey ?? $"field.{typeof(TData).Name}.{property.Name}");
    }
}
