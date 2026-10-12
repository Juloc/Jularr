using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Localization;
using Jularr.Backend.Data.Fields.Localization;

namespace Jularr.Backend.Data.Admin.Localization.V1
{
    public sealed class ReadUiMessagesParametersV1 : BaseParameters
    {
        public ReadUiMessagesParametersV1(long uiLocaleId, int page = 1, int pageSize = 25)
        {
            UiLocaleId = uiLocaleId;
            Page = page;
            PageSize = pageSize;
        }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }

        public int Page { get; }
        public int PageSize { get; }
    }

    public sealed class UiMessageParametersV1 : BaseParameters
    {
        public UiMessageParametersV1(long uiTranslationMessageId, long uiLocaleId)
        {
            UiTranslationMessageId = uiTranslationMessageId;
            UiLocaleId = uiLocaleId;
        }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Id))]
        public long UiTranslationMessageId { get; }

        [DataField(typeof(UiLocaleFields), nameof(UiLocaleFields.Id))]
        public long UiLocaleId { get; }
    }

    public sealed class UpdateUiDescriptionParametersV1 : BaseParameters
    {
        public UpdateUiDescriptionParametersV1(long uiTranslationMessageId)
        {
            UiTranslationMessageId = uiTranslationMessageId;
        }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Id))]
        public long UiTranslationMessageId { get; }
    }

    public sealed class UiMessageDetailsV1 : BaseResponse
    {
        public UiMessageDetailsV1(long id, string key, string englishText, string? description, long version, string? text, UiTranslationStatusType? uiTranslationStatusTypeId,
            long? translationVersion, long? messageVersion)
        {
            Id = id;
            Key = key;
            EnglishText = englishText;
            Description = description;
            Version = version;
            Text = text;
            UiTranslationStatusTypeId = uiTranslationStatusTypeId;
            TranslationVersion = translationVersion;
            MessageVersion = messageVersion;
        }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Id))]
        public long Id { get; }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Key))]
        public string Key { get; }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.EnglishText))]
        public string EnglishText { get; }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Description))]
        public string? Description { get; }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Version))]
        public long Version { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.Text))]
        public string? Text { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.UiTranslationStatusTypeId), false)]
        public UiTranslationStatusType? UiTranslationStatusTypeId { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.Version), false)]
        public long? TranslationVersion { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.MessageVersion), false)]
        public long? MessageVersion { get; }
    }

    public sealed class UpdateUiDescriptionDataV1 : BaseData
    {
        public UpdateUiDescriptionDataV1(long messageVersion, string? description)
        {
            MessageVersion = messageVersion;
            Description = description;
        }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Version))]
        public long MessageVersion { get; }

        [DataField(typeof(UiTranslationMessageFields), nameof(UiTranslationMessageFields.Description))]
        public string? Description { get; }
    }

    public sealed class SaveUiTranslationDataV1 : BaseData
    {
        public SaveUiTranslationDataV1(long messageVersion, long? translationVersion, string text)
        {
            MessageVersion = messageVersion;
            TranslationVersion = translationVersion;
            Text = text;
        }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.MessageVersion))]
        public long MessageVersion { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.Version), false)]
        public long? TranslationVersion { get; }

        [DataField(typeof(UiTranslationFields), nameof(UiTranslationFields.Text), true)]
        public string Text { get; }
    }
}
