using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Generic two-area layout. Optional Master/Detail behavior is a mode,
    // never an unrelated feature-sized component.
    public sealed class Split : IUiComponent
    {
        public List<IUiComponent> Primary { get; } = [];
        public List<IUiComponent> Secondary { get; } = [];

        private bool _masterDetail;
        private bool _detailOpen;
        private string? _backHref;

        public Split(Action<Split> define) => define(this);

        // Selection is owned by the page/Backend; it supplies detailOpen and
        // a real master-list return route. Split does not invent client state.
        public Split MasterDetail(bool detailOpen = false, string? backHref = null)
        {
            if (detailOpen && string.IsNullOrWhiteSpace(backHref))
                throw new ArgumentException("An open mobile detail requires a return URL.", nameof(backHref));

            _masterDetail = true;
            _detailOpen = detailOpen;
            _backHref = backHref;
            return this;
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var classes = "split";
            if (Secondary.Count == 0) classes += " split--single";
            if (_masterDetail) classes += " split--master-detail";
            if (_detailOpen) classes += " split--detail-open";

            var areas = new List<IUiComponent>
            {
                new Section([.. Primary]).Class("split__primary")
            };
            if (Secondary.Count > 0)
            {
                var details = new List<IUiComponent>();
                if (_masterDetail && _backHref is not null)
                {
                    details.Add(new A(new Text("nav.back")).Attribute("href", _backHref)
                        .Class("split__back"));
                }
                details.AddRange(Secondary);
                areas.Add(new Section([.. details]).Class("split__secondary"));
            }

            new Div([.. areas]).Class(classes).WriteTo(writer, encoder, texts);
        }
    }
}
