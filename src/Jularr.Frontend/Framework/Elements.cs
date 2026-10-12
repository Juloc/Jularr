using System.Text.Encodings.Web;

namespace Jularr.Frontend.Framework
{
    // Minimal server-side HTML DSL, not a browser DOM or client-side framework.
    public class HtmlElement : IUiComponent
    {
        private static readonly HashSet<string> VoidTags = ["input", "img", "br", "hr"];
        private readonly string _tag;
        private readonly IUiComponent[] _children;
        private readonly List<(string Name, string Value)> _attributes = [];

        protected HtmlElement(string tag, params IUiComponent[] children)
        {
            if (string.IsNullOrEmpty(tag) || !char.IsAsciiLetterLower(tag[0]) || tag.Any(character => !char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character)))
            {
                throw new ArgumentException("Invalid HTML element.", nameof(tag));
            }

            _tag = tag;
            _children = children;
        }

        public HtmlElement Attribute(string name, string value)
        {
            if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetterLower(name[0]) || name[0] == '_')
                || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                || name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name == "style")
            {
                throw new ArgumentException("Unsafe HTML attribute.", nameof(name));
            }

            if (name is "href" or "src" or "action" or "formaction" or "poster" && !IsSafeUrl(value))
            {
                throw new ArgumentException("Unsafe URL.", nameof(value));
            }

            var index = _attributes.FindIndex(attribute => attribute.Name == name);
            if (index >= 0) _attributes[index] = (name, value);
            else _attributes.Add((name, value));
            return this;
        }

        public HtmlElement Class(string value) => Attribute("class", value);

        // Browsers ignore control characters and spaces in schemes; only web and mail schemes or relative URLs pass.
        private static bool IsSafeUrl(string value)
        {
            var cleaned = new string(value.Where(character => character > ' ').ToArray());
            var colon = cleaned.IndexOf(':');
            var end = cleaned.IndexOfAny(['/', '?', '#']);
            if (colon < 0 || end >= 0 && end < colon)
            {
                return true;
            }

            return cleaned[..colon].ToLowerInvariant() is "http" or "https" or "mailto";
        }

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            writer.Write('<');
            writer.Write(_tag);
            foreach (var (name, value) in _attributes)
            {
                writer.Write(' ');
                writer.Write(name);
                writer.Write("=\"");
                encoder.Encode(writer, value);
                writer.Write('"');
            }

            writer.Write('>');
            if (VoidTags.Contains(_tag))
            {
                if (_children.Length > 0) throw new InvalidOperationException("Void elements cannot have children.");
                return;
            }

            foreach (var child in _children)
            {
                child.WriteTo(writer, encoder, texts);
            }

            writer.Write("</");
            writer.Write(_tag);
            writer.Write('>');
        }
    }

    // Small HTML primitives are kept together; feature components own their renderers.
    public sealed class Div(params IUiComponent[] children) : HtmlElement("div", children);
    public sealed class H1(params IUiComponent[] children) : HtmlElement("h1", children);
    public sealed class H2(params IUiComponent[] children) : HtmlElement("h2", children);
    public sealed class H3(params IUiComponent[] children) : HtmlElement("h3", children);
    public sealed class P(params IUiComponent[] children) : HtmlElement("p", children);
    public sealed class Span(params IUiComponent[] children) : HtmlElement("span", children);
    public sealed class Section(params IUiComponent[] children) : HtmlElement("section", children);
    public sealed class Header(params IUiComponent[] children) : HtmlElement("header", children);
    public sealed class Nav(params IUiComponent[] children) : HtmlElement("nav", children);
    public sealed class Ul(params IUiComponent[] children) : HtmlElement("ul", children);
    public sealed class Li(params IUiComponent[] children) : HtmlElement("li", children);
    public sealed class A(params IUiComponent[] children) : HtmlElement("a", children);
    public sealed class Input() : HtmlElement("input");
    public sealed class Img() : HtmlElement("img");
    public sealed class TableElement(params IUiComponent[] children) : HtmlElement("table", children);
    public sealed class Thead(params IUiComponent[] children) : HtmlElement("thead", children);
    public sealed class Tbody(params IUiComponent[] children) : HtmlElement("tbody", children);
    public sealed class Tr(params IUiComponent[] children) : HtmlElement("tr", children);
    public sealed class Th(params IUiComponent[] children) : HtmlElement("th", children);
    public sealed class Video(params IUiComponent[] children) : HtmlElement("video", children);

    public sealed class Text(string resourceKey) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) =>
            encoder.Encode(writer, texts[resourceKey]);
    }

    // Encode runtime data, not resource keys.
    public sealed class ValueText(string value) : IUiComponent
    {
        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts) => encoder.Encode(writer, value);
    }
}
