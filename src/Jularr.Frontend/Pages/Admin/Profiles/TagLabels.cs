using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    // The name of a tag on a page: a person's tag by its label, a tag Jularr keeps itself by what it stands for, in the person's language.
    internal static class TagLabels
    {
        public static string Label(AcquisitionTagDetailsV1 tag, UiText texts)
        {
            if (tag.AutoKey is not { } key) return tag.Label;
            var value = key[(key.IndexOf(':') + 1)..];
            return key[..key.IndexOf(':')] switch
            {
                "kind" when Enum.GetValues<AcquisitionMediaKind>().FirstOrDefault(kind => string.Equals(kind.ToString(), value, StringComparison.OrdinalIgnoreCase)) is var kind && kind != default
                    => texts["library.scope." + ProfilesModel.KindSlug(kind)],
                "language" => string.Format(CultureInfo.CurrentCulture, texts["arr.tag.auto.language"], Work.WorkLanguages.Name(value)),
                "genre" when byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && Enum.IsDefined((Genre)id)
                    => string.Format(CultureInfo.CurrentCulture, texts["arr.tag.auto.genre"], texts["discover.genre." + (Genre)id]),
                "requester" => string.Format(CultureInfo.CurrentCulture, texts["arr.tag.auto.requester"], tag.AutoName ?? "?"),
                _ => value
            };
        }

        // A choice of a picker: a kept tag says it is automatic.
        public static SelectOption Choice(AcquisitionTagDetailsV1 tag, UiText texts) =>
            new(tag.Id.ToString(CultureInfo.InvariantCulture), tag.AutoKey is null ? tag.Label : string.Format(CultureInfo.CurrentCulture, texts["arr.tag.auto"], Label(tag, texts)));
    }
}
