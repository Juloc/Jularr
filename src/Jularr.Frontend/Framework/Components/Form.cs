using System.Linq.Expressions;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;

namespace Jularr.Frontend.Framework;

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

// Metadata-aware form structure only. An editable, submitting Form must be backed by a service.
public sealed class Form<TData> : IUiComponent where TData : BaseData
{
    public FormFields<TData> Fields { get; } = new();

    public Form(Action<Form<TData>> define) => define(this);

    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var rows = new List<IUiComponent>();
        foreach (var field in Fields.Items)
        {
            var input = new Input().Attribute("type", field.Control switch
            {
                FieldControl.Password => "password",
                FieldControl.Number => "number",
                FieldControl.Checkbox => "checkbox",
                _ => "text"
            }).Attribute("name", field.Name).Attribute("disabled", "disabled");

            if (field.Required) input.Attribute("required", "required");
            if (field.MaximumLength is { } max) input.Attribute("maxlength", max.ToString());
            if (field.Minimum is { } min) input.Attribute("min", min.ToString());
            if (field.Maximum is { } limit) input.Attribute("max", limit.ToString());

            var label = new HtmlLabel(new Text(field.LabelKey)).Attribute("for", field.Name);
            input.Attribute("id", field.Name);
            rows.Add(new Div(label, input).Class("ui-field"));
        }

        new HtmlForm([.. rows]).Class("ui-form").WriteTo(writer, encoder, texts);
    }

    private sealed class HtmlLabel(params IUiComponent[] children) : HtmlElement("label", children);
    private sealed class HtmlForm(params IUiComponent[] children) : HtmlElement("form", children);
}
