using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Labelled numbers and states as a description list.
    public sealed class FactList(IEnumerable<(string LabelKey, IUiComponent Value)> facts) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            new Dl([.. facts.Select(fact => (IUiComponent)new Div(new Dt(new Text(fact.LabelKey)), new Dd(fact.Value)).Class("fact-list__fact"))]).Class("fact-list").WriteTo(writer, encoder, texts);
    }
}
