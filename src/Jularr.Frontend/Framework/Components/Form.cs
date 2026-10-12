using System.Globalization;
using System.Linq.Expressions;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Service.Core;

namespace Jularr.Frontend.Framework
{
    public sealed class Form<TData> : IUiComponent where TData : BaseData
    {
        private static long _nextId;
        private readonly List<IUiComponent> _content = [];
        private readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        private readonly string _id = "form-" + typeof(TData).FullName + "-" + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        private readonly string? _action;
        private readonly TData? _data;
        private readonly ServiceError? _error;

        public Form(Action<Form<TData>> define, string? action = null, string? antiforgeryToken = null, TData? data = null, ServiceError? error = null)
        {
            // Bind only local POST actions.
            if (action is not null && (!action.StartsWith('/') || action.StartsWith("//") || action.Any(character => char.IsControl(character) || character == '\\')
                || string.IsNullOrWhiteSpace(antiforgeryToken)))
            {
                throw new ArgumentException("Forms require a local action and an antiforgery token.", nameof(action));
            }

            _action = action;
            _data = data;
            _error = error;
            if (action is not null)
            {
                _content.Add(new Input().Attribute("type", "hidden").Attribute("name", "__RequestVerificationToken").Attribute("value", antiforgeryToken!));
            }

            define(this);
        }

        public Form<TData> Text<TValue>(Expression<Func<TData, TValue>> field, string labelKey, string autocomplete = "off") => Add(field, FieldControl.Text, labelKey, autocomplete);
        public Form<TData> Password<TValue>(Expression<Func<TData, TValue>> field, string labelKey, string autocomplete = "off") => Add(field, FieldControl.Password, labelKey, autocomplete);
        public Form<TData> Number<TValue>(Expression<Func<TData, TValue>> field, string labelKey) => Add(field, FieldControl.Number, labelKey, "off");

        public Form<TData> Multiline<TValue>(Expression<Func<TData, TValue>> selector, string labelKey)
        {
            var field = Declare(selector, FieldControl.Text, labelKey);
            var input = new HtmlTextarea(new ValueText(Value(field) ?? "")).Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name).Attribute("rows", "5");
            Configure(field, input);
            AddField(field, input);
            return this;
        }

        public Form<TData> Select<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, IEnumerable<SelectOption> options)
        {
            var field = Declare(selector, FieldControl.Select, labelKey);
            var value = Value(field);
            var items = options.Select(option =>
            {
                var element = new HtmlOption(new ValueText(option.Label)).Attribute("value", option.Value);
                if (option.Value == value)
                {
                    element.Attribute("selected", "selected");
                }

                return element;
            }).ToArray();
            var select = new HtmlSelect(items).Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name);
            Configure(field, select);
            AddField(field, select);
            return this;
        }

        public Form<TData> Checkbox(string labelKey)
        {
            _content.Add(new HtmlLabel(new Input().Attribute("type", "checkbox").Attribute("disabled", "disabled"), new Span(new Text(labelKey))).Class("ui-checkbox"));
            return this;
        }

        public Form<TData> Hidden<TValue>(Expression<Func<TData, TValue>> selector)
        {
            var field = Declare(selector, FieldControl.Text, "");
            _content.Add(new Input().Attribute("type", "hidden").Attribute("name", field.Name).Attribute("value", Value(field) ?? ""));
            return this;
        }

        public Form<TData> Submit(string key, string? value = null)
        {
            var label = value is null ? (IUiComponent)new Text(key) : new Span(new Text(key), new Span(new ValueText(value)));
            var button = new Button(label).Primary();
            if (_action is null) button.Disabled();
            else button.Submit();

            _content.Add(button);
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var content = new List<IUiComponent>(_content);
            if (_error is not null && (_error.Field is null || !_fields.Contains(_error.Field)))
            {
                content.Insert(0, new P(new Text(_error.FieldResourceKey ?? _error.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            }

            var form = new HtmlForm([.. content]).Class("ui-form");
            if (_action is not null)
            {
                form.Attribute("action", _action).Attribute("method", "post");
            }

            form.WriteTo(writer, encoder, texts);
        }

        private Form<TData> Add<TValue>(Expression<Func<TData, TValue>> selector, FieldControl control, string labelKey, string autocomplete)
        {
            var field = Declare(selector, control, labelKey);
            var input = new Input().Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name)
                .Attribute("type", control == FieldControl.Password ? "password" : control == FieldControl.Number ? "number" : "text").Attribute("autocomplete", autocomplete);

            // Never render submitted passwords.
            if (control != FieldControl.Password && Value(field) is { } value)
            {
                input.Attribute("value", value);
            }

            Configure(field, input);
            AddField(field, input);
            return this;
        }

        private BaseField Declare<TValue>(Expression<Func<TData, TValue>> selector, FieldControl control, string labelKey)
        {
            var field = BaseField.From(selector, control, labelKey);
            if (!_fields.Add(field.Name))
            {
                throw new InvalidOperationException("A form field cannot be declared twice.");
            }

            return field;
        }

        private string? Value(BaseField field)
        {
            if (_data is null) return null;

            // Preserve IDs and concurrency values.
            return field.ReadValue(_data) switch
            {
                DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
                Enum catalogValue => Convert.ToByte(catalogValue).ToString(CultureInfo.InvariantCulture),
                var value => Convert.ToString(value, CultureInfo.InvariantCulture)
            };
        }

        private void Configure(BaseField field, HtmlElement input)
        {
            // Use canonical field metadata.
            if (_action is null) input.Attribute("disabled", "disabled");
            if (field.Required) input.Attribute("required", "required");
            if (field.MinimumLength is { } minimumLength) input.Attribute("minlength", minimumLength.ToString(CultureInfo.InvariantCulture));
            if (field.MaximumLength is { } length) input.Attribute("maxlength", length.ToString(CultureInfo.InvariantCulture));
            if (field.Minimum is { } minimum) input.Attribute("min", minimum.ToString(CultureInfo.InvariantCulture));
            if (field.Maximum is { } maximum) input.Attribute("max", maximum.ToString(CultureInfo.InvariantCulture));
            if (field.Scale is { } scale) input.Attribute("step", (1m / (decimal)Math.Pow(10, scale)).ToString(CultureInfo.InvariantCulture));
            if (_error?.Field == field.Name)
            {
                input.Attribute("aria-invalid", "true").Attribute("aria-describedby", _id + "-" + field.Name + "-error");
            }
        }

        private void AddField(BaseField field, HtmlElement input)
        {
            var content = new List<IUiComponent> { new HtmlLabel(new Text(field.LabelKey)).Attribute("for", _id + "-" + field.Name), input };
            if (_error?.Field == field.Name)
            {
                content.Add(new P(new Text(_error.FieldResourceKey ?? _error.ResourceKey)).Class("field-error").Attribute("id", _id + "-" + field.Name + "-error").Attribute("role", "alert"));
            }

            _content.Add(new Div([.. content]).Class("ui-field"));
        }

        private sealed class HtmlLabel(params IUiComponent[] children) : HtmlElement("label", children);
        private sealed class HtmlForm(params IUiComponent[] children) : HtmlElement("form", children);
        private sealed class HtmlSelect(params IUiComponent[] children) : HtmlElement("select", children);
        private sealed class HtmlOption(params IUiComponent[] children) : HtmlElement("option", children);
        private sealed class HtmlTextarea(params IUiComponent[] children) : HtmlElement("textarea", children);
    }

    public sealed record SelectOption(string Value, string Label);
}
