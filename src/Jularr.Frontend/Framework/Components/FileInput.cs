using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The one file field of Jularr: a drop zone with a button, then the chosen file with its size and a remove button. The native file field
    // stays inside (it opens the system dialog and carries the file in the form; without script it shows as the field, styled like the others).
    public sealed class FileInput(HtmlElement native, string accept, long maximumBytes, FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var limit = string.Join(", ", accept.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(entry => entry.TrimStart('.').ToUpperInvariant()));
            var maximum = ByteSize.Format(maximumBytes);
            var zone = new Div(
                new Span(new Icon("document", assets, 24)).Class("file-input__tile"),
                new Strong(new Text("fileInput.drop")).Class("file-input__title"),
                new Span(new ValueText(limit.Length == 0 ? string.Format(CultureInfo.CurrentCulture, texts["fileInput.maximum"], maximum)
                    : string.Format(CultureInfo.CurrentCulture, texts["fileInput.limit"], limit, maximum))).Class("file-input__limit"),
                new Button(new Icon("plus", assets, 14), new Text("fileInput.choose")).Soft().Attribute("data-file-choose", "")).Class("file-input__zone");
            var chosen = new Div(
                new Icon("document", assets, 18),
                new Span(new Span().Class("truncated__text").Attribute("data-file-name", ""), new Span().Class("truncated__tip").Attribute("aria-hidden", "true").Attribute("data-file-tip", ""))
                    .Class("truncated file-input__name"),
                new Span().Class("file-input__size").Attribute("data-file-size", ""),
                new Button(new Icon("close", assets, 16)).IconOnly(texts["fileInput.remove"]).Attribute("data-file-remove", ""))
                .Class("file-input__file").Attribute("hidden", "hidden").Attribute("data-file-chosen", "");
            new Div(native.Class("file-input__native"), zone, chosen, new P().Class("field-error file-input__error").Attribute("aria-live", "polite").Attribute("hidden", "hidden").Attribute("data-file-error", ""))
                .Class("file-input").Attribute("data-file-input", "").Attribute("data-file-accept", accept).Attribute("data-file-max", maximumBytes.ToString(CultureInfo.InvariantCulture))
                .TextAttribute("data-file-empty", "fileInput.empty").TextAttribute("data-file-large", "fileInput.tooLarge").TextAttribute("data-file-type", "fileInput.wrongType")
                .WriteTo(writer, encoder, texts);
        }
    }
}
