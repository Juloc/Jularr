using System.Linq.Expressions;
using System.Reflection;
using Jularr.Data.Common;
using Jularr.Data.Fields;

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

public sealed class FormFields<TData> where TData : BaseData
{
    public List<BaseField> Items { get; } = [];

    public void Text<TValue>(Expression<Func<TData, TValue>> field) => Add(field, FieldControl.Text);
    public void Password<TValue>(Expression<Func<TData, TValue>> field) => Add(field, FieldControl.Password);
    public void TextArea<TValue>(Expression<Func<TData, TValue>> field) => Add(field, FieldControl.TextArea);
    public void Number<TValue>(Expression<Func<TData, TValue>> field) => Add(field, FieldControl.Number);
    public void Checkbox<TValue>(Expression<Func<TData, TValue>> field) => Add(field, FieldControl.Checkbox);
    public void Select<TValue>(Expression<Func<TData, TValue>> field) => Add(field, FieldControl.Select);
    public void MultiSelect<TValue>(Expression<Func<TData, TValue>> field) => Add(field, FieldControl.MultiSelect);

    private void Add<TValue>(Expression<Func<TData, TValue>> selector, FieldControl control)
    {
        var field = BaseField.From(selector, control);
        if (Items.Any(item => item.Name == field.Name))
        {
            throw new InvalidOperationException("A field may only be declared once.");
        }

        Items.Add(field);
    }
}

public sealed class Form<TData> : IUiComponent where TData : BaseData
{
    public FormFields<TData> Fields { get; } = new();
    public bool CanSave { get; private set; }

    public Form(Action<Form<TData>> define)
    {
        define(this);
    }

    public void Save() => CanSave = true;
}
