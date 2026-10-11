using System.Linq.Expressions;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;

namespace Jularr.Frontend.Framework;

// Typed, presentation-only form. Until a real handler is bound, every control is
// disabled and no submit URL/action is synthesized.
public sealed class Form<TData> : IUiComponent where TData : BaseData
{
    private readonly List<IUiComponent> _content = [];
    private readonly HashSet<string> _fields = new(StringComparer.Ordinal);
    private readonly string _id = "form-" + typeof(TData).Name;

    public Form(Action<Form<TData>> define) => define(this);

    public Form<TData> Text<TValue>(
        Expression<Func<TData, TValue>> field, string labelKey, string autocomplete = "off") =>
        Add(field, FieldControl.Text, labelKey, autocomplete);

    public Form<TData> Password<TValue>(
        Expression<Func<TData, TValue>> field, string labelKey, string autocomplete = "off") =>
        Add(field, FieldControl.Password, labelKey, autocomplete);

    public Form<TData> Number<TValue>(
        Expression<Func<TData, TValue>> field, string labelKey) =>
        Add(field, FieldControl.Number, labelKey, "off");

    public Form<TData> Checkbox(string labelKey)
    {
        _content.Add(new HtmlLabel(
            new Input().Attribute("type", "checkbox").Attribute("disabled", "disabled"),
            new Span(new Text(labelKey))
        ).Class("ui-checkbox"));
        return this;
    }

    public Form<TData> Submit(string key)
    {
        _content.Add(new Button(new Text(key)).Class("button button-primary")
            .Attribute("type", "button").Attribute("disabled", "disabled"));
        return this;
    }

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
        new HtmlForm([.. _content]).Class("ui-form")
            .WriteTo(writer, encoder, texts);

    private Form<TData> Add<TValue>(
        Expression<Func<TData, TValue>> selector, FieldControl control, string labelKey, string autocomplete)
    {
        var field = BaseField.From(selector, control, labelKey);
        if (!_fields.Add(field.Name))
            throw new InvalidOperationException("A form field cannot be declared twice.");

        var id = _id + "-" + field.Name;
        var input = new Input()
            .Attribute("id", id)
            .Attribute("name", field.Name)
            .Attribute("type", control == FieldControl.Password ? "password"
                : control == FieldControl.Number ? "number" : "text")
            .Attribute("autocomplete", autocomplete)
            .Attribute("disabled", "disabled");
        if (field.Required) input.Attribute("required", "required");
        if (field.MaximumLength is { } length)
            input.Attribute("maxlength", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (field.Minimum is { } minimum)
            input.Attribute("min", minimum.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (field.Maximum is { } maximum)
            input.Attribute("max", maximum.ToString(System.Globalization.CultureInfo.InvariantCulture));

        _content.Add(new Div(
            new HtmlLabel(new Text(field.LabelKey)).Attribute("for", id),
            input
        ).Class("ui-field"));
        return this;
    }

    private sealed class HtmlLabel(params IUiComponent[] children) : HtmlElement("label", children);
    private sealed class HtmlForm(params IUiComponent[] children) : HtmlElement("form", children);
}
