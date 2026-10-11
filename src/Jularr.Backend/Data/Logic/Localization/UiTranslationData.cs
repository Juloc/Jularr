using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Logic.Localization
{
    public sealed class UiDescriptionUpdateData : BaseData
    {
        public UiDescriptionUpdateData(long uiTranslationMessageId, long messageVersion, string? description)
        {
            UiTranslationMessageId = uiTranslationMessageId;
            MessageVersion = messageVersion;
            Description = description;
        }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Id))]
        public long UiTranslationMessageId { get; }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Version))]
        public long MessageVersion { get; }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Description))]
        public string? Description { get; }
    }

    public sealed class UiTranslationSaveData : BaseData
    {
        public UiTranslationSaveData(long uiLocaleId, long uiTranslationMessageId,
            long messageVersion, string text, UiTranslationStatusType uiTranslationStatusTypeId)
        {
            UiLocaleId = uiLocaleId;
            UiTranslationMessageId = uiTranslationMessageId;
            MessageVersion = messageVersion;
            Text = text;
            UiTranslationStatusTypeId = uiTranslationStatusTypeId;
        }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.UiLocaleId))]
        public long UiLocaleId { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.UiTranslationMessageId))]
        public long UiTranslationMessageId { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.MessageVersion))]
        public long MessageVersion { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.Text), true)]
        public string Text { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.UiTranslationStatusTypeId))]
        public UiTranslationStatusType UiTranslationStatusTypeId { get; }
    }
}
