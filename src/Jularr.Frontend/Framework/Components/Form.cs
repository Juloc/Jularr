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
        private readonly HashSet<string> _hiddenFields = new(StringComparer.Ordinal);
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

        // Digits on a numeric keypad; a PIN of a known length is entered first and confirms itself.
        public Form<TData> Pin<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, int? length = null)
        {
            var field = Declare(selector, FieldControl.Password, labelKey);
            var input = new Input().Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name).Attribute("type", "password")
                .Attribute("autocomplete", length is null ? "new-password" : "off").Attribute("inputmode", "numeric").Attribute("pattern", "[0-9]*");
            Configure(field, input);
            if (length is { } digits)
            {
                var value = digits.ToString(CultureInfo.InvariantCulture);
                input.Attribute("maxlength", value).Attribute("data-pin-length", value).Attribute("autofocus", "autofocus");
            }

            AddField(field, input);
            return this;
        }

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

        public Form<TData> Choices<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, IEnumerable<SelectOption> options)
        {
            // Several values under one name.
            var field = Declare(selector, FieldControl.MultiSelect, labelKey);
            var selected = (_data is null ? null : field.ReadValue(_data) as IEnumerable<string>)?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var choices = options.Select(option =>
            {
                var input = new Input().Attribute("type", "checkbox").Attribute("name", field.Name).Attribute("value", option.Value);
                if (selected.Contains(option.Value)) input.Attribute("checked", "checked");
                if (_action is null) input.Attribute("disabled", "disabled");
                return new HtmlLabel(input, new Span(new ValueText(option.Label))).Class("ui-checkbox");
            }).ToArray<IUiComponent>();
            var content = new List<IUiComponent> { new HtmlLegend(new Text(field.LabelKey)), new Div(choices).Class("ui-choices") };
            if (_error?.Field == field.Name)
            {
                content.Add(new P(new Text(_error.FieldResourceKey ?? _error.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            }

            _content.Add(new HtmlFieldset([.. content]).Class("ui-field"));
            return this;
        }

        public Form<TData> Checkbox(string labelKey)
        {
            _content.Add(new HtmlLabel(new Input().Attribute("type", "checkbox").Attribute("disabled", "disabled"), new Span(new Text(labelKey))).Class("ui-checkbox"));
            return this;
        }

        public Form<TData> Toggle(Expression<Func<TData, bool>> selector, string labelKey)
        {
            // Bind a canonical Boolean field.
            var field = Declare(selector, FieldControl.Checkbox, labelKey);
            var toggle = new Toggle(labelKey, _data is not null && field.ReadValue(_data) is true);
            toggle.Control.Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name);
            Configure(field, toggle.Control);

            // First value wins when checked.
            IUiComponent control = _action is null ? toggle : new Div(toggle,
                new Input().Attribute("type", "hidden").Attribute("name", field.Name).Attribute("value", "false"));
            AddField(field, control);
            return this;
        }

        public Form<TData> Hidden<TValue>(Expression<Func<TData, TValue>> selector)
        {
            var field = Declare(selector, FieldControl.Text, "");
            _hiddenFields.Add(field.Name);
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
            if (_error is not null && (_error.Field is null || !_fields.Contains(_error.Field) || _hiddenFields.Contains(_error.Field)))
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

            // ASP.NET invariant form binding.
            var valueType = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
            if (_action is not null && (field.Minimum.HasValue || valueType == typeof(DateTime) || valueType == typeof(DateTimeOffset)))
            {
                _content.Add(new Input().Attribute("type", "hidden").Attribute("name", "__Invariant").Attribute("value", field.Name));
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
                string[] list => string.Join(", ", list),
                int[] list => string.Join(", ", list.Select(item => item.ToString(CultureInfo.InvariantCulture))),
                Enum catalogValue => Convert.ToByte(catalogValue).ToString(CultureInfo.InvariantCulture),
                var value => Convert.ToString(value, CultureInfo.InvariantCulture)
            };
        }

        private void Configure(BaseField field, HtmlElement input)
        {
            // Use canonical field metadata.
            if (_action is null) input.Attribute("disabled", "disabled");
            if (field.Required && field.Control != FieldControl.Checkbox) input.Attribute("required", "required");
            if (field.MinimumLength is { } minimumLength) input.Attribute("minlength", minimumLength.ToString(CultureInfo.InvariantCulture));
            if (field.MaximumLength is { } length && !field.ValueType.IsArray) input.Attribute("maxlength", length.ToString(CultureInfo.InvariantCulture));
            if (field.Minimum is { } minimum) input.Attribute("min", minimum.ToString(CultureInfo.InvariantCulture));
            if (field.Maximum is { } maximum) input.Attribute("max", maximum.ToString(CultureInfo.InvariantCulture));
            if (field.Scale is { } scale) input.Attribute("step", (1m / (decimal)Math.Pow(10, scale)).ToString(CultureInfo.InvariantCulture));
            if (_error?.Field == field.Name)
            {
                input.Attribute("aria-invalid", "true").Attribute("aria-describedby", _id + "-" + field.Name + "-error");
            }
        }

        private void AddField(BaseField field, IUiComponent input)
        {
            // Label stays on the outline.
            var content = new List<IUiComponent>
            {
                field.Control == FieldControl.Checkbox ? input : new HtmlFieldset(
                    new HtmlLegend(new HtmlLabel(new Text(field.LabelKey)).Class("ui-field__label").Attribute("for", _id + "-" + field.Name)),
                    input).Class("ui-field__control")
            };
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
        private sealed class HtmlFieldset(params IUiComponent[] children) : HtmlElement("fieldset", children);
        private sealed class HtmlLegend(params IUiComponent[] children) : HtmlElement("legend", children);
    }

    public sealed record SelectOption(string Value, string Label);
}
