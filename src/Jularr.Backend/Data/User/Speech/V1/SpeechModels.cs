using System.Text.Json.Serialization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Speech;
using Jularr.Backend.Data.Enums.Speech;

namespace Jularr.Backend.Data.User.Speech.V1
{
    public sealed class SpeechModelsParametersV1 : BaseParameters;

    public sealed class SpeechModelParametersV1(string hash) : BaseParameters
    {
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Hash))]
        public string Hash { get; } = hash;
    }

    public sealed class SpeechModelV1 : BaseResponse
    {
        public SpeechModelV1(string id, string version, string name, string language, string voice, string license, string licenseUrl, string source, long sizeBytes,
            string sha256, bool isEnabled, long revision, bool requiresLicenseAcceptance = false, string? sampleUrl = null,
            SpeechVoiceGenderType gender = SpeechVoiceGenderType.Unknown, string? genderSource = null, bool isInstalled = false)
        {
            Id = id;
            Version = version;
            Name = name;
            Language = language;
            Voice = voice;
            License = license;
            LicenseUrl = licenseUrl;
            Source = source;
            SizeBytes = sizeBytes;
            Sha256 = sha256;
            IsEnabled = isEnabled;
            Revision = revision;
            RequiresLicenseAcceptance = requiresLicenseAcceptance;
            SampleUrl = sampleUrl;
            Gender = gender;
            GenderSource = genderSource;
            IsInstalled = isInstalled;
        }

        [DataField(typeof(SpeechFields), nameof(SpeechFields.Id))]
        public string Id { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Id))]
        public string Version { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Name))]
        public string Name { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Language))]
        public string Language { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Id))]
        public string Voice { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Name))]
        public string License { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Url))]
        public string LicenseUrl { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Url))]
        public string Source { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.SizeBytes))]
        public long SizeBytes { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Hash))]
        public string Sha256 { get; }
        public string DownloadUrl => "/api/v1/speech/models/" + Sha256 + "/file";
        public bool IsEnabled { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Version))]
        public long Revision { get; }
        public bool RequiresLicenseAcceptance { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Url), false)]
        public string? SampleUrl { get; }
        public SpeechVoiceGenderType Gender { get; }
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Url), false)]
        public string? GenderSource { get; }
        public bool IsInstalled { get; }
    }

    public sealed class SpeechModelFileV1(Stream content) : BaseResponse, IImmutableFileOutput
    {
        [JsonIgnore]
        public Stream Content { get; } = content;
        [JsonIgnore]
        public string ContentType => "application/octet-stream";
    }
}
