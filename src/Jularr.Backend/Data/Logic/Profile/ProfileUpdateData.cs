using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;

namespace Jularr.Backend.Data.Logic.Profile
{
    public sealed class ProfileUpdateData : BaseData
    {
        public ProfileUpdateData(long id, string displayName, long uiLocaleId) : this(id, displayName, uiLocaleId, null, null)
        {
        }

        public ProfileUpdateData(long id, string displayName, long uiLocaleId, string? firstContentLanguage, string? secondContentLanguage)
        {
            FirstContentLanguage = firstContentLanguage;
            SecondContentLanguage = secondContentLanguage;
            Id = id;
            DisplayName = displayName;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
        public long Id { get; internal set; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
        public string DisplayName { get; internal set; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.UiLocaleId))]
        public long UiLocaleId { get; internal set; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.FirstContentLanguage))]
        public string? FirstContentLanguage { get; internal set; }

        [DataField(typeof(ProfileFields), nameof(ProfileFields.SecondContentLanguage))]
        public string? SecondContentLanguage { get; internal set; }
    }
}
