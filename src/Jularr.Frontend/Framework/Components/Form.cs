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
        private List<IUiComponent> _actions = [];
        private List<IUiComponent> _target;
        private readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hiddenFields = new(StringComparer.Ordinal);
        private readonly string _id = "form-" + typeof(TData).FullName + "-" + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        private readonly string? _action;
        private readonly TData? _data;
        private readonly ServiceError? _error;
        private bool _isUpload;
        private int _visibleFields;
        private bool _hasWideField;
        private readonly string _prefix;
        private readonly string? _antiforgeryToken;
        private readonly bool _local;
        private bool _saves;

        public Form(Action<Form<TData>> define, string? action = null, string? antiforgeryToken = null, TData? data = null, ServiceError? error = null, bool local = false)
            : this(define, action, antiforgeryToken, data, error, "", true, local)
        {
        }

        private Form(Action<Form<TData>> define, string? action, string? antiforgeryToken, TData? data, ServiceError? error, string prefix, bool ownsToken, bool local)
        {
            if (local && (action is not null || antiforgeryToken is not null))
            {
                throw new ArgumentException("Local fields cannot declare a server action or antiforgery token.", nameof(local));
            }

            // Bind only local POST actions.
            if (action is not null && (!action.StartsWith('/') || action.StartsWith("//") || action.Any(character => char.IsControl(character) || character == '\\')
                || string.IsNullOrWhiteSpace(antiforgeryToken)))
            {
                throw new ArgumentException("Forms require a local action and an antiforgery token.", nameof(action));
            }

            _target = _content;
            _action = action;
            _data = data;
            _error = error;
            _prefix = prefix;
            _antiforgeryToken = antiforgeryToken;
            _local = local;
            if (action is not null && ownsToken)
            {
                _content.Add(new Input().Attribute("type", "hidden").Attribute("name", "__RequestVerificationToken").Attribute("value", antiforgeryToken!));
            }

            define(this);
        }

        // Rows share the parent form and retain their IDs; ASP.NET binds their indexed names.
        public Form<TData> Rows<TItem>(Expression<Func<TData, BaseDataList<TItem>>> selector, Action<Form<TItem>, TItem> define, Func<TItem, bool>? include = null) where TItem : BaseData
        {
            if (selector.Body is not MemberExpression { Member: System.Reflection.PropertyInfo property }) throw new ArgumentException("Rows must select a data list.", nameof(selector));
            var rows = _data is null ? null : property.GetValue(_data) as BaseDataList<TItem>;
            for (var index = 0; index < (rows?.Count ?? 0); index++)
            {
                var item = rows![index];
                if (include is not null && !include(item)) continue;
                var child = new Form<TItem>(form => define(form, item), _action, _antiforgeryToken, item, _error,
                    _prefix + property.Name + "[" + index.ToString(CultureInfo.InvariantCulture) + "].", false, _local);
                if (child._actions.Count > 0) throw new InvalidOperationException("Row fields share the parent's submit action.");
                if (child._visibleFields > 0) _target.Add(new Div([.. child._content]).Class("ui-form__row"));
                else _target.AddRange(child._content);
                _fields.UnionWith(child._fields);
                _hiddenFields.UnionWith(child._hiddenFields);
            }

            _hasWideField = true;
            return this;
        }

        // A form may name another example or explanation than the field's own where the field means something special there.
        // An affix puts an icon at the start of the field and a quiet unit at its end, inside the field; validation and stepping stay the field's own.
        public Form<TData> Text<TValue>(Expression<Func<TData, TValue>> field, string labelKey, string autocomplete = "off", FieldHelp? help = null, FieldAffix? affix = null) =>
            Add(field, FieldControl.Text, labelKey, autocomplete, help, affix);
        public Form<TData> Password<TValue>(Expression<Func<TData, TValue>> field, string labelKey, string autocomplete = "off", FieldHelp? help = null) =>
            Add(field, FieldControl.Password, labelKey, autocomplete, help);
        public Form<TData> Number<TValue>(Expression<Func<TData, TValue>> field, string labelKey, FieldHelp? help = null, FieldAffix? affix = null) =>
            Add(field, FieldControl.Number, labelKey, "off", help, affix);

        public Form<TData> Range<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, FieldHelp? help = null, FieldAffix? affix = null)
        {
            var field = Declare(selector, FieldControl.Range, labelKey, help);
            if (!field.Required || field.ValueType != typeof(int) && field.ValueType != typeof(long) && field.ValueType != typeof(decimal)
                || field.Minimum is not { } minimum || field.Maximum is not { } maximum || minimum >= maximum)
            {
                throw new InvalidOperationException("Range controls require a required numeric field with increasing minimum and maximum bounds.");
            }

            var input = new Input().Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name).Attribute("type", "range").Attribute("step", "1")
                .Attribute("data-range-precision", (field.Scale ?? 0).ToString(CultureInfo.InvariantCulture));
            if (Value(field) is { } value) input.Attribute("value", value);
            Configure(field, input);
            var readout = new List<IUiComponent>
            {
                input,
                new HtmlOutput(new ValueText(_data is null ? "" : Convert.ToString(field.ReadValue(_data), CultureInfo.CurrentCulture) ?? ""))
                    .Attribute("for", _id + "-" + field.Name).Attribute("aria-hidden", "true")
            };
            if (affix?.SuffixKey is { } unit) readout.Add(new Span(new Text(unit)).Class("ui-range__unit"));
            AddField(field, new Div([.. readout]).Class(affix?.SuffixKey is null ? "ui-range" : "ui-range ui-range--unit"));
            return this;
        }

        // A wall-clock time: hour and minute fields on a desktop, a wheel on Android, the system's own picker on iOS (TimePicker.entry.ts).
        public Form<TData> Time<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, FieldHelp? help = null)
        {
            var field = Declare(selector, FieldControl.Text, labelKey, help);
            var input = new Input().Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name).Attribute("type", "time").Attribute("data-time-picker", "")
                .TextAttribute("data-time-hour", "form.time.hour").TextAttribute("data-time-minute", "form.time.minute").TextAttribute("data-time-done", "form.time.done");
            if (Value(field) is { } value) input.Attribute("value", value);
            Configure(field, input);
            AddField(field, input);
            return this;
        }

        public Form<TData> Color(Expression<Func<TData, string>> selector, string labelKey)
        {
            var field = Declare(selector, FieldControl.Text, labelKey);
            var input = new Input().Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name).Attribute("type", "color");
            if (Value(field) is { } value) input.Attribute("value", value);
            Configure(field, input);
            AddField(field, input);
            return this;
        }

        public Form<TData> ByteSize<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, FieldHelp? help = null)
        {
            var field = Declare(selector, FieldControl.Number, labelKey, help);
            var input = new Input().Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name).Attribute("type", "number")
                .Attribute("value", Value(field) ?? "").Attribute("data-byte-input", "");
            Configure(field, input);
            var units = new HtmlSelect([.. new[] { ("1", "B"), ("1048576", "MiB"), ("1073741824", "GiB"), ("1099511627776", "TiB") }
                .Select(unit => new HtmlOption(new ValueText(unit.Item2)).Attribute("value", unit.Item1))])
                .Attribute("disabled", "disabled").Attribute("data-byte-unit", "").TextAttribute("aria-label", "form.sizeUnit");
            AddField(field, new Div(input, units).Class("ui-form__size").Attribute("data-byte-size", "").TextAttribute("data-byte-invalid", "form.invalid_type"));
            return this;
        }

        // A file field is the file input (drop zone, chosen file, remove) and makes the form a multipart upload.
        public Form<TData> File<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, FrontendAssets assets)
        {
            var field = Declare(selector, FieldControl.File, labelKey);
            var input = new Input().Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name).Attribute("type", "file");
            if (field.Accept is { } accept) input.Attribute("accept", accept);
            Configure(field, input);
            AddField(field, new FileInput(input, field.Accept ?? "", field.MaximumBytes ?? 0, assets), wide: true, bare: true);
            _isUpload = true;
            return this;
        }

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

        public Form<TData> Multiline<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, FieldHelp? help = null)
        {
            var field = Declare(selector, FieldControl.Text, labelKey, help);
            var value = _data is not null && field.ReadValue(_data) is string[] lines ? string.Join("\n", lines) : Value(field) ?? "";
            var input = new HtmlTextarea(new ValueText(value)).Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name).Attribute("rows", "5");
            Configure(field, input);
            AddField(field, input, wide: true);
            return this;
        }

        public Form<TData> Select<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, IEnumerable<SelectOption> options, FieldHelp? help = null,
            string? refreshAction = null, string? refreshLabelKey = null, FieldAffix? affix = null)
        {
            var field = Declare(selector, FieldControl.Select, labelKey, help);
            var value = Value(field);
            var choices = options.ToArray();

            // Up to three short fixed choices, such as yes or no, sit side by side as one segmented control; data lists stay a select.
            var valueType = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
            if ((valueType == typeof(bool) || valueType.IsEnum) && choices.Length is > 1 and <= MaxSegments && choices.All(option => option.Label.Length <= MaxSegmentLength))
            {
                var segments = choices.Select((option, index) =>
                {
                    var radio = new Input().Attribute("type", "radio").Attribute("id", _id + "-" + field.Name + "-" + index.ToString(CultureInfo.InvariantCulture))
                        .Attribute("name", field.Name).Attribute("value", option.Value);
                    if (option.Value == value) radio.Attribute("checked", "checked");
                    Configure(field, radio);
                    if (refreshAction is not null) radio.Attribute("data-form-refresh", refreshAction);
                    return new HtmlLabel(radio, new Span(new ValueText(option.Label))).Class("ui-segment");
                }).ToArray<IUiComponent>();
                AddField(field, new Div(segments).Class("ui-segments"), segmented: true);
                RefreshButton(refreshAction, refreshLabelKey);
                return this;
            }

            var items = choices.Select(option =>
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
            if (refreshAction is not null) select.Attribute("data-form-refresh", refreshAction);
            AddField(field, select, affix: affix);
            RefreshButton(refreshAction, refreshLabelKey);
            return this;
        }

        private void RefreshButton(string? action, string? labelKey)
        {
            if (action is null) return;
            if (_local) throw new InvalidOperationException("Local fields cannot refresh from a server action.");
            if (labelKey is null || !action.StartsWith('/') || action.StartsWith("//")) throw new ArgumentException("Refresh requires a local action and a label.");
            _target.Add(new Button(new Text(labelKey)).Attribute("type", "submit").Attribute("formaction", action)
                .Attribute("formnovalidate", "formnovalidate").Attribute("data-form-refresh-submit", action));
        }

        public Form<TData> Choices<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, IEnumerable<SelectOption> options, FieldHelp? help = null)
        {
            // Several values under one name.
            var field = Declare(selector, FieldControl.MultiSelect, labelKey, help);
            var selected = (_data is null ? null : field.ReadValue(_data) as System.Collections.IEnumerable)?.Cast<object>()
                .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var choices = options.Select(option => new Checkbox(new ValueText(option.Label), field.Name, option.Value, selected.Contains(option.Value), _action is null && !_local))
                .ToArray<IUiComponent>();
            var content = new List<IUiComponent> { new HtmlLegend(new Text(field.LabelKey), new FieldInfo(field.InfoKey, _id + "-" + field.Name + "-info")), new Div(choices).Class("ui-choices") };
            if (_error?.Field == field.Name)
            {
                content.Add(new P(new Text(_error.FieldResourceKey ?? _error.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            }

            _target.Add(new HtmlFieldset([.. content]).Class("ui-field ui-field--wide"));
            _visibleFields++;
            _hasWideField = true;
            return this;
        }

        // Rows the person puts in order, each with a switch: the keys in their order under the field's name, the keys of the switched-on rows under
        // `shownName`. The surrounding section names the list, so it shows no label of its own.
        public Form<TData> Sortable<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, FrontendAssets assets, IReadOnlyList<SortableItem> items, string shownName,
            string stateOnKey, string stateOffKey)
        {
            var field = Declare(selector, FieldControl.MultiSelect, labelKey);
            var content = new List<IUiComponent> { new SortableList(assets, field.Name, _prefix + shownName, labelKey, items, stateOnKey, stateOffKey, _action is null && !_local) };
            if (_error?.Field == field.Name)
            {
                content.Add(new P(new Text(_error.FieldResourceKey ?? _error.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            }

            _target.Add(new Div([.. content]).Class("ui-field ui-field--wide"));
            _visibleFields++;
            _hasWideField = true;
            return this;
        }

        // A titled group of fields in its own panel; the form keeps one save.
        public Form<TData> Section(string titleKey, Action<Form<TData>> define)
        {
            var outer = _target;
            var outerActions = _actions;
            var inner = new List<IUiComponent> { new HtmlLegend(new Text(titleKey)) };
            _target = inner;
            _actions = [];
            define(this);

            // Buttons declared inside a section close that section.
            if (_actions.Count > 0) inner.Add(new Div([.. _actions]).Class("ui-form__actions"));
            _target = outer;
            _actions = outerActions;
            _target.Add(new HtmlFieldset([.. inner]).Class("ui-form__section"));
            return this;
        }

        public Form<TData> Content(IUiComponent component)
        {
            _target.Add(component);
            _hasWideField = true;
            return this;
        }

        // A column of a choice table: several values, or one with an optional none.
        public ChoiceColumn Column<TValue>(Expression<Func<TData, TValue>> selector, string labelKey, bool single = false, string? noneKey = null)
        {
            var field = Declare(selector, single ? FieldControl.Select : FieldControl.MultiSelect, labelKey);
            var selected = single
                ? Value(field) is { Length: > 0 } value ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value } : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (_data is null ? null : field.ReadValue(_data) as System.Collections.IEnumerable)?.Cast<object>()
                    .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)!).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            return new ChoiceColumn(field.Name, labelKey, single, noneKey, selected);
        }

        // One row per option, with optional commands that share the parent form.
        public Form<TData> ChoiceTable(string labelKey, IReadOnlyList<SelectOption> rows, IReadOnlyList<ChoiceColumn> columns, Func<SelectOption, IUiComponent>? actions = null)
        {
            // Each choice is named by its row and its column.
            string RowId(int index) => _id + "-row-" + index.ToString(CultureInfo.InvariantCulture);
            string ColumnId(ChoiceColumn column) => _id + "-column-" + column.Name;
            HtmlElement Choice(ChoiceColumn column, string value, int row, bool none = false)
            {
                var input = new Input().Attribute("type", column.Single ? "radio" : "checkbox").Attribute("name", column.Name).Attribute("value", value);
                if (column.Selected.Contains(value) || none && column.Selected.Count == 0) input.Attribute("checked", "checked");
                input.Attribute("aria-labelledby", RowId(row) + " " + ColumnId(column));
                if (_action is null && !_local) input.Attribute("disabled", "disabled");
                return new HtmlCell(input);
            }

            var head = new HtmlRow([new HtmlHeader(new Span()), .. columns.Select(column => new HtmlHeader(new Text(column.LabelKey)).Attribute("id", ColumnId(column))),
                .. (actions is null ? [] : new[] { new HtmlHeader(new Text("common.actions")) })]);
            var body = rows.Select((row, index) => (IUiComponent)new HtmlRow([new HtmlHeader(new ValueText(row.Label)).Attribute("scope", "row").Attribute("id", RowId(index)),
                .. columns.Select(column => Choice(column, row.Value, index)), .. (actions is null ? [] : new[] { new HtmlCell(actions(row)) })])).ToList();

            // A single column without a value, such as no cutoff.
            if (columns.FirstOrDefault(column => column.Single && column.NoneKey is not null) is { } optional)
            {
                body.Add(new HtmlRow([new HtmlHeader(new Text(optional.NoneKey!)).Attribute("scope", "row").Attribute("id", RowId(rows.Count)),
                    .. columns.Select(column => column == optional ? Choice(column, "", rows.Count, none: true) : new HtmlCell()), .. (actions is null ? [] : new[] { new HtmlCell() })]));
            }

            var content = new List<IUiComponent>
            {
                // The columns and the surrounding section name what is chosen.
                new HtmlLegend(new Text(labelKey)).Class("visually-hidden"),
                new Div(new HtmlTable(new HtmlHead(head), new HtmlBody([.. body]))).Class("ui-choice-table")
            };
            if (_error?.Field is { } errorField && columns.Any(column => column.Name == errorField))
            {
                content.Add(new P(new Text(_error.FieldResourceKey ?? _error.ResourceKey)).Class("field-error").Attribute("role", "alert"));
            }

            _target.Add(new HtmlFieldset([.. content]).Class("ui-field ui-field--wide"));
            _visibleFields++;
            _hasWideField = true;
            return this;
        }

        public Form<TData> Checkbox(string labelKey)
        {
            _target.Add(new Checkbox(new Text(labelKey), disabled: true));
            return this;
        }

        public Form<TData> Toggle(Expression<Func<TData, bool>> selector, string labelKey, FieldHelp? help = null)
        {
            // Bind a canonical Boolean field.
            var field = Declare(selector, FieldControl.Checkbox, labelKey, help);
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
            _target.Add(new Input().Attribute("type", "hidden").Attribute("name", field.Name).Attribute("value", Value(field) ?? ""));
            return this;
        }

        // A hidden value names the row for screen readers when many rows share one action.
        public Form<TData> Submit(string key, string? value = null, bool valueHidden = false)
        {
            var label = value is null ? (IUiComponent)new Text(key)
                : new Span(new Text(key), valueHidden ? new Span(new ValueText(" " + value)).Class("visually-hidden") : new Span(new ValueText(value)));
            var button = new Button(label).Primary();
            return Submit(button);
        }

        // A note at the start of the buttons' row, such as what the buttons would act on.
        public Form<TData> Note(IUiComponent note)
        {
            _actions.Add(new Div(note).Class("ui-form__note"));
            return this;
        }

        // The page's save pill saves this form together with every other changed one; without script the button saves it alone.
        public Form<TData> Save()
        {
            _saves = true;
            var button = new Button(new Text("common.save")).Primary();
            button.Attribute("data-form-save", "");
            return Submit(button);
        }

        // A dialog's form closes with its own Cancel beside the primary action.
        public Form<TData> Cancel()
        {
            _actions.Add(new Button(new Text("common.cancel")).Attribute("data-dialog-close", ""));
            return this;
        }

        public Form<TData> Submit(Button button)
        {
            if (_local) throw new InvalidOperationException("Local fields cannot declare a submit action.");
            if (_action is null) button.Disabled();
            else button.Submit();

            _actions.Add(button);
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var content = new List<IUiComponent>(_content);
            if (_error is not null && (_error.Field is null || !_fields.Contains(_error.Field) || _hiddenFields.Contains(_error.Field)))
            {
                content.Insert(0, new P(ErrorText.Of(_error)).Class("field-error").Attribute("role", "alert"));
            }

            // Buttons close the form; a single short field, or none, sits beside its buttons.
            if (_actions.Count > 0) content.Add(new Div([.. _actions]).Class("ui-form__actions"));
            // Local preferences are an editable field group, never a submitting form.
            HtmlElement container = _local ? new Div([.. content]) : new HtmlForm([.. content]);
            var form = container.Class(_visibleFields <= 1 && !_hasWideField ? "ui-form ui-form--inline" : "ui-form")
                .Attribute("data-form-kind", typeof(TData).FullName!);
            if (_local) form.Attribute("data-form-local", "");
            if (_action is not null)
            {
                form.Attribute("action", _action).Attribute("method", "post");
                if (_isUpload) form.Attribute("enctype", "multipart/form-data");
                if (_saves) form.Attribute("data-save", "");
            }

            form.WriteTo(writer, encoder, texts);
        }

        private Form<TData> Add<TValue>(Expression<Func<TData, TValue>> selector, FieldControl control, string labelKey, string autocomplete, FieldHelp? help, FieldAffix? affix = null)
        {
            var field = Declare(selector, control, labelKey, help);
            var input = new Input().Attribute("id", _id + "-" + field.Name).Attribute("name", field.Name)
                .Attribute("type", control == FieldControl.Password ? "password" : control == FieldControl.Number ? "number" : "text").Attribute("autocomplete", autocomplete);

            // Never render submitted passwords.
            if (control != FieldControl.Password && Value(field) is { } value)
            {
                input.Attribute("value", value);
            }

            Configure(field, input);
            AddField(field, input, wide: control == FieldControl.Text && (field.ValueType.IsArray || field.MaximumLength is null or > WideLength), stepper: control == FieldControl.Number, affix: affix, help: help);
            return this;
        }

        private BaseField Declare<TValue>(Expression<Func<TData, TValue>> selector, FieldControl control, string labelKey, FieldHelp? help = null)
        {
            var field = BaseField.From(selector, control, labelKey);
            field = field with { Name = _prefix + field.Name, PlaceholderKey = help?.Placeholder ?? field.PlaceholderKey, InfoKey = help?.Info ?? field.InfoKey };
            if (!_fields.Add(field.Name))
            {
                throw new InvalidOperationException("A form field cannot be declared twice.");
            }

            // ASP.NET invariant form binding.
            var valueType = Nullable.GetUnderlyingType(field.ValueType) ?? field.ValueType;
            if (_action is not null && (field.Minimum.HasValue || valueType == typeof(DateTime) || valueType == typeof(DateTimeOffset)))
            {
                _target.Add(new Input().Attribute("type", "hidden").Attribute("name", "__Invariant").Attribute("value", field.Name));
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
                TimeOnly time => time.ToString("HH:mm", CultureInfo.InvariantCulture),
                string[] list => string.Join(", ", list),
                int[] list => string.Join(", ", list.Select(item => item.ToString(CultureInfo.InvariantCulture))),
                Enum catalogValue => Convert.ToByte(catalogValue).ToString(CultureInfo.InvariantCulture),
                var value => Convert.ToString(value, CultureInfo.InvariantCulture)
            };
        }

        private void Configure(BaseField field, HtmlElement input)
        {
            // Use canonical field metadata.
            if (_action is null && !_local) input.Attribute("disabled", "disabled");
            // A list may stay empty; it is required only to be sent.
            if (field.Required && field.Control != FieldControl.Checkbox && !field.ValueType.IsArray) input.Attribute("required", "required");
            if (field.MinimumLength is { } minimumLength) input.Attribute("minlength", minimumLength.ToString(CultureInfo.InvariantCulture));
            if (field.MaximumLength is { } length && !field.ValueType.IsArray) input.Attribute("maxlength", length.ToString(CultureInfo.InvariantCulture));
            if (field.Minimum is { } minimum) input.Attribute("min", minimum.ToString(CultureInfo.InvariantCulture));
            if (field.Maximum is { } maximum) input.Attribute("max", maximum.ToString(CultureInfo.InvariantCulture));
            if (field.IsUsername) input.Attribute("pattern", @"[A-Za-z0-9._\-]{3,32}").Attribute("spellcheck", "false").Attribute("autocapitalize", "off");
            if (field.IsAddress) input.Attribute("inputmode", "url").Attribute("spellcheck", "false").Attribute("autocapitalize", "off");

            // Typed fields show an example of their form, an optional one what happens when it stays empty; a password shows only the hint a page names for it.
            if (field.Control is FieldControl.Text or FieldControl.Number or FieldControl.Password) input.TextAttribute("placeholder", field.PlaceholderKey);
            if (field.Scale is { } scale) input.Attribute("step", (1m / (decimal)Math.Pow(10, scale)).ToString(CultureInfo.InvariantCulture));
            if (_error?.Field == field.Name)
            {
                input.Attribute("aria-invalid", "true").Attribute("aria-describedby", _id + "-" + field.Name + "-error");
            }
        }

        private HtmlElement NumberStepper() => new Div([.. new[] { 1, -1 }.Select(delta => (IUiComponent)new Button(new ValueText(delta < 0 ? "−" : "+")).Class("ui-field__step")
            .Attribute("data-number-step", delta < 0 ? "-1" : "1").TextAttribute("aria-label", delta < 0 ? "form.number.decrease" : "form.number.increase")
            .Attribute("tabindex", "-1"))]).Class("ui-field__stepper").Attribute("hidden", "hidden");

        // Lists and longer texts, such as addresses, get the whole row.
        private const int WideLength = 200;
        private const int MaxSegments = 3;
        private const int MaxSegmentLength = 20;

        private void AddField(BaseField field, IUiComponent input, bool wide = false, bool segmented = false, bool stepper = false, FieldAffix? affix = null, FieldHelp? help = null, bool bare = false)
        {
            // The label sits above the field (the mockups' `.field label`); a segmented control is a group named by its legend.
            var info = new FieldInfo(field.InfoKey, _id + "-" + field.Name + "-info");
            var content = new List<IUiComponent>();
            if (field.Control == FieldControl.Checkbox) content.Add(input);
            else if (segmented)
            {
                content.Add(new HtmlFieldset(new HtmlLegend(new Span(new Text(field.LabelKey)).Class("ui-field__label"), info).Class("ui-field__top"),
                    new Div(input).Class("ui-field__control ui-field__control--segmented")).Class("ui-field__group"));
            }
            else
            {
                content.Add(new Div([new HtmlLabel(new Text(field.LabelKey)).Class("ui-field__label").Attribute("for", _id + "-" + field.Name), info,
                    .. help?.Aside is { } aside ? new[] { aside } : []]).Class("ui-field__top"));
                if (bare)
                {
                    content.Add(input);
                }
                else
                {
                    // A number steps with its own plus and minus (shown by script; the browser's spin buttons are hidden).
                    // An icon leads the field and a unit follows it, before the stepper.
                    var parts = new List<IUiComponent>();
                    if (affix?.Prefix is { } prefix) parts.Add(new Span(prefix).Class("ui-field__prefix").Attribute("aria-hidden", "true"));
                    parts.Add(input);
                    if (affix?.SuffixKey is { } suffix) parts.Add(new Span(new Text(suffix)).Class("ui-field__suffix"));
                    if (stepper) parts.Add(NumberStepper());
                    if (help?.Trailing is { } trailing) parts.Add(trailing);
                    content.Add(new Div([.. parts]).Class("ui-field__control" + (stepper ? " ui-field__control--number" : "") + (affix is null ? "" : " ui-field__control--affixed") + (help?.Trailing is null ? "" : " ui-field__control--trailing")));
                }
                if (help?.Note is { } note) content.Add(note);
            }
            if (field.Control == FieldControl.Checkbox) content.Add(new FieldInfo(field.InfoKey, _id + "-" + field.Name + "-info"));
            if (_error?.Field == field.Name)
            {
                content.Add(new P(new Text(_error.FieldResourceKey ?? _error.ResourceKey)).Class("field-error").Attribute("id", _id + "-" + field.Name + "-error").Attribute("role", "alert"));
            }

            _target.Add(new Div([.. content]).Class(wide ? "ui-field ui-field--wide" : "ui-field"));
            _visibleFields++;
            _hasWideField |= wide;
        }

        private sealed class HtmlLabel(params IUiComponent[] children) : HtmlElement("label", children);
        private sealed class HtmlForm(params IUiComponent[] children) : HtmlElement("form", children);
        private sealed class HtmlSelect(params IUiComponent[] children) : HtmlElement("select", children);
        private sealed class HtmlOption(params IUiComponent[] children) : HtmlElement("option", children);
        private sealed class HtmlTextarea(params IUiComponent[] children) : HtmlElement("textarea", children);
        private sealed class HtmlFieldset(params IUiComponent[] children) : HtmlElement("fieldset", children);
        private sealed class HtmlLegend(params IUiComponent[] children) : HtmlElement("legend", children);
        private sealed class HtmlOutput(params IUiComponent[] children) : HtmlElement("output", children);
        private sealed class HtmlTable(params IUiComponent[] children) : HtmlElement("table", children);
        private sealed class HtmlHead(params IUiComponent[] children) : HtmlElement("thead", children);
        private sealed class HtmlBody(params IUiComponent[] children) : HtmlElement("tbody", children);
        private sealed class HtmlRow(params IUiComponent[] children) : HtmlElement("tr", children);
        private sealed class HtmlHeader(params IUiComponent[] children) : HtmlElement("th", children);
        private sealed class HtmlCell(params IUiComponent[] children) : HtmlElement("td", children);
    }

    // A field shown as one column of a choice table.
    public sealed class ChoiceColumn
    {
        internal ChoiceColumn(string name, string labelKey, bool single, string? noneKey, IReadOnlySet<string> selected)
        {
            Name = name;
            LabelKey = labelKey;
            Single = single;
            NoneKey = noneKey;
            Selected = selected;
        }

        internal string Name { get; }
        internal string LabelKey { get; }
        internal bool Single { get; }
        internal string? NoneKey { get; }
        internal IReadOnlySet<string> Selected { get; }
    }

    public sealed record SelectOption(string Value, string Label);

    // What a field wears inside its frame: an icon circle at the start (built by the page, which owns the icon sprite) and a unit text key at the end.
    public sealed record FieldAffix(IUiComponent? Prefix = null, string? SuffixKey = null)
    {
        public static FieldAffix Unit(string suffixKey) => new(null, suffixKey);
    }
}
