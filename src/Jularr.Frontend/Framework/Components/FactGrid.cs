using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // The facts of one title as a panel of labelled values in two columns with hairlines between them; a value that is plain text ends in an ellipsis and shows whole in a tip.
    public sealed class FactGrid(IReadOnlyList<(string LabelKey, IUiComponent Value)> facts) : IUiComponent
    {
        public bool HasItems => facts.Count > 0;

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            if (!HasItems) return;

            new Dl([.. facts.Select(fact => (IUiComponent)new Div(new Dt(new Text(fact.LabelKey)), new Dd(fact.Value)).Class("fact-grid__fact"))]).Class("fact-grid").WriteTo(writer, encoder, texts);
        }
    }
}
