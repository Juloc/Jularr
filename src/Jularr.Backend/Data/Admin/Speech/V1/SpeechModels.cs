using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Speech;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.User.Speech.V1;

namespace Jularr.Backend.Data.Admin.Speech.V1
{
    public sealed class SearchSpeechModelsParametersV1(string? query = null, string? language = null, int page = 1, int pageSize = 25,
        SpeechVoiceGenderType? gender = null, bool? isInstalled = null) : BaseParameters
    {
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Query))]
        public string? Query { get; } = query;
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Query))]
        public string? Language { get; } = language;
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Gender))]
        public SpeechVoiceGenderType? Gender { get; } = gender;
        [DataField(typeof(SpeechFields), nameof(SpeechFields.IsInstalled))]
        public bool? IsInstalled { get; } = isInstalled;
        public int Page { get; } = page;
        public int PageSize { get; } = pageSize;
    }

    public sealed class SpeechModelCatalogV1(PageResult<SpeechModelV1> models, IReadOnlyList<string> languages) : BaseResponse
    {
        public PageResult<SpeechModelV1> Models { get; } = models;
        public IReadOnlyList<string> Languages { get; } = languages;
    }

    public sealed class UpdateSpeechModelDataV1 : BaseData
    {
        public UpdateSpeechModelDataV1(bool isEnabled, long version, bool acceptLicense = false)
        {
            IsEnabled = isEnabled;
            Version = version;
            AcceptLicense = acceptLicense;
        }

        [DataField(typeof(SpeechFields), nameof(SpeechFields.IsEnabled))]
        public bool IsEnabled { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Version))]
        public long Version { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.AcceptLicense))]
        public bool AcceptLicense { get; }
    }
}
