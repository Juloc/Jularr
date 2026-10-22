using System.Globalization;
using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    public enum StepState
    {
        Upcoming,
        Current,
        Done
    }

    // One step of a Stepper: its label, where it leads (none where it cannot be reached yet) and how far along it is.
    public sealed record StepperStep(string LabelKey, string? Href, StepState State);

    // The steps of a flow in one row: a numbered bubble and a name each, a check on the bubbles already passed and the accent on the current one.
    public sealed class Stepper(IReadOnlyList<StepperStep> steps, string ariaLabelKey, FrontendAssets assets) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (steps.Count == 0) return;

            var items = steps.Select((step, index) =>
            {
                var bubble = new Span(step.State == StepState.Done ? new Icon("check", assets, 15, "2.4") : new ValueText((index + 1).ToString(CultureInfo.CurrentCulture)))
                    .Class("stepper__bubble");
                var content = new IUiComponent[] { bubble, new Span(new Text(step.LabelKey)).Class("stepper__label") };
                var item = step.Href is null ? new Span(content).Class("stepper__item") : new A(content).Attribute("href", step.Href).Class("stepper__item");
                if (step.State == StepState.Current) item.Attribute("aria-current", "step");
                return (IUiComponent)new Li(item).Class("stepper__step stepper__step--" + step.State.ToString().ToLowerInvariant());
            }).ToArray();

            new Nav(new Ol(items).Class("stepper__list")).Class("stepper").Attribute("aria-label", texts[ariaLabelKey]).WriteTo(writer, encoder, texts);
        }
    }
}
