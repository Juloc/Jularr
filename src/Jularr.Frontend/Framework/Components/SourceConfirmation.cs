using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Shared, browser-native source confirmation: no JavaScript or embedded third-party pages.
    public sealed class SourceConfirmation(IUiComponent openSource, IUiComponent confirmationForm) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            new SourceDetails(
                new SourceSummary(
                    new Span(new Text("source.assistance.title")).Class("source-assistance__title"),
                    new Span(new Text("source.assistance.open")).Class("source-assistance__indicator")).Class("source-assistance__summary"),
                new Div(
                    new P(new Text("source.assistance.description")),
                    new P(new Text("source.assistance.step1")).Class("source-assistance__step"),
                    new Div(openSource).Class("source-assistance__open"),
                    new P(new Text("source.assistance.step2")).Class("source-assistance__step"),
                    new Button(new Text("source.assistance.paste")).Attribute("data-source-paste", "true"),
                    confirmationForm).Class("source-assistance__body")).Class("source-assistance")
                .WriteTo(writer, encoder, texts);
        }

        private sealed class SourceDetails(params IUiComponent[] children) : HtmlElement("details", children);
        private sealed class SourceSummary(params IUiComponent[] children) : HtmlElement("summary", children);
    }
}
