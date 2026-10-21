using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // A sample of text such as JSON: closed behind a button until asked for, then a read-only box with a copy button.
    public sealed class CodeSample(string openKey, string code, FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // Copying needs script, so the button appears only when it can work.
            var copy = new Button(new Span(new Icon("copy", assets, 16)).Class("code-sample__copy-icon"), new Span(new Icon("check", assets, 16)).Class("code-sample__done-icon"))
                .IconOnly(texts["codeSample.copy"]).Class("button button--icon code-sample__copy").Attribute("hidden", "hidden").Attribute("data-code-copy", "").Attribute("data-copied", texts["codeSample.copied"]).Attribute("data-copy", texts["codeSample.copy"]);
            new Details(
                new Summary(new Span(new Text(openKey)), new Icon("chevron-down", assets, 14)).Class("button button--soft code-sample__toggle"),
                new Div(copy, new Pre(new Span(new ValueText(code)).Attribute("data-code-text", "")).Attribute("tabindex", "0")).Class("code-sample__box"))
                .Class("code-sample").WriteTo(writer, encoder, texts);
        }
    }
}
