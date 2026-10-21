using System.Text.Encodings.Web;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Modules.Appearance
{
    public sealed class Appearance(bool collapsible = false) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            // Independent style and brightness.
            var content = new Div(
                Group("appearance.style", "skin", texts, ("original", "appearance.original"), ("clean", "appearance.clean")),
                Group("appearance.brightness", "mode", texts, ("system", "appearance.system"), ("light", "appearance.light"), ("dark", "appearance.dark")),
                new Div(
                    new Label(new Text("appearance.accent"), new Input().Attribute("type", "color").Attribute("value", "#8040f7")
                        .Attribute("data-accent-input", "true").Attribute("aria-label", texts["appearance.accent"]),
                        new Span().Attribute("data-accent-value", "true")).Class("appearance__color"),
                    new Div(
                        new Button(new Text("appearance.jularrRed")).Attribute("data-set-accent", "#c8102e").Attribute("aria-pressed", "false"),
                        new Button(new Text("appearance.jularrPurple")).Attribute("data-set-accent", "#8040f7").Attribute("aria-pressed", "false"),
                        new Button(new Text("appearance.accentDefault")).Attribute("data-reset-accent", "true")).Class("appearance__choices"),
                    new P(new Text("appearance.devicePreference")).Class("appearance__hint")))
                .Class("appearance__options");

            if (collapsible)
            {
                new Details(new Summary(new Text("appearance.title")), content).Class("appearance")
                    .WriteTo(writer, encoder, texts);
            }
            else
            {
                content.WriteTo(writer, encoder, texts);
            }
        }

        private static IUiComponent Group(string labelKey, string kind, UiText texts, params (string Value, string LabelKey)[] options) =>
            new Div(new Span(new Text(labelKey)).Class("appearance__label"),
                new Div(options.Select(option => (IUiComponent)new Button(new Text(option.LabelKey))
                    .Attribute("data-set-" + kind, option.Value).Attribute("aria-pressed", "false")).ToArray())
                    .Class("appearance__choices"))
                .Class("appearance__group").Attribute("role", "group").Attribute("aria-label", texts[labelKey]);

        private sealed class Label(params IUiComponent[] content) : HtmlElement("label", content);
    }
}
