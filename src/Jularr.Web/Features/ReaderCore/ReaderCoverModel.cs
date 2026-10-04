using System.Globalization;

namespace Jularr.Web.Features.ReaderCore;

/// <summary>
/// The small cover in the reader top bar. Without a known cover URL, or when the image cannot be
/// loaded, the title's initial is drawn instead of a broken image.
/// </summary>
public sealed record ReaderCoverModel(string Title, string? ImageUrl)
{
    public string Initial
    {
        get
        {
            var title = Title.Trim();
            return title.Length == 0 ? string.Empty : StringInfo.GetNextTextElement(title).ToUpperInvariant();
        }
    }
}
