using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework;

public sealed record UiSettingRow(string Key, string? DescriptionKey = null);

public sealed class UiSettingsGroup(string headingKey, IReadOnlyList<UiSettingRow> rows) : IUiComponent
{
    public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
    {
        var items = rows.Select(row =>
        {
            var children = new List<IUiComponent> { new Span(new Text(row.Key)).Class("settings-row-label") };
            if (row.DescriptionKey is not null)
                children.Add(new Span(new Text(row.DescriptionKey)).Class("settings-row-description"));
            return (IUiComponent)new Li([.. children]);
        }).ToArray();

        new Section(new H2(new Text(headingKey)), new Ul(items))
            .Class("settings-group").WriteTo(writer, encoder, texts);
    }
}
