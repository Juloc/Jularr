using System.Text.Json;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Modules.Speech
{
    // Profile and Reader use the same bound fields; installation remains a separate device action.
    public sealed class SpeechSettings(SpeechPreferencesV1 preferences, string language, string action, string token, ServiceError? error = null, SaveSpeechPreferencesDataV1? draft = null,
        IReadOnlyList<SpeechModelV1>? models = null) : IUiComponent
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public void WriteTo(TextWriter writer, HtmlEncoder encoder, UiText texts)
        {
            var choice = preferences.Voices.FirstOrDefault(item => item.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                ?? preferences.Voices.FirstOrDefault(item => item.Language.Equals(language.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                ?? preferences.Voices.FirstOrDefault(item => item.Language.Split('-')[0].Equals(language.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            var input = draft ?? new SaveSpeechPreferencesDataV1(preferences.Rate, preferences.Pitch, preferences.Volume, preferences.Version, language,
                choice?.ProviderTypeId ?? SpeechProviderType.Device, choice?.ModelId, choice?.VoiceId);
            var voices = new List<SelectOption> { new("", texts["speech.voice.default"]) };
            if (input.VoiceId is { } voice) voices.Add(new SelectOption(voice, voice));
            var available = models ?? [];
            var providers = new[] { new SelectOption("1", texts["speech.provider.device"]), new SelectOption("2", texts["speech.provider.neural"]) };
            var modelOptions = new List<SelectOption> { new("", texts["speech.model.choose"]) };
            modelOptions.AddRange(available.Select(model => new SelectOption(model.Id, model.Name + " · " + model.Language)));
            if (input.ModelId is { } modelId && !available.Any(model => model.Id == modelId)) modelOptions.Add(new SelectOption(modelId, modelId));
            new Div(
                new P(new Text("speech.privacy")),
                new P(new Text("speech.voicesUnavailable")).Attribute("data-speech-voices-status", "").Attribute("role", "status"),
                new Form<SaveSpeechPreferencesDataV1>(form =>
                {
                    form.Select(item => item.Language, "speech.language", new[] { input.Language, language }.Concat(preferences.Voices.Select(item => item.Language))
                        .Concat(available.Select(model => model.Language)).Distinct(StringComparer.OrdinalIgnoreCase).Select(code => new SelectOption(code, code)));
                    form.Hidden(item => item.Version);
                    form.Select(item => item.ProviderTypeId, "speech.provider", providers);
                    form.Select(item => item.ModelId, "speech.model", modelOptions);
                    form.Select(item => item.VoiceId, "speech.voice", voices);
                    form.Range(item => item.Rate, "speech.rate", affix: FieldAffix.Unit("unit.times"));
                    form.Range(item => item.Pitch, "speech.pitch", affix: FieldAffix.Unit("unit.times"));
                    form.Range(item => item.Volume, "speech.volume");
                    form.Save();
                }, action, token, input, error),
                new P(new Text("speech.pitchUnavailable")).Attribute("data-speech-pitch-note", "").Attribute("hidden", "hidden"),
                new P().Attribute("data-speech-model-status", "").Attribute("role", "status"),
                new P(new A().Attribute("data-speech-model-license", "").Attribute("hidden", "hidden").Attribute("target", "_blank").Attribute("rel", "noopener noreferrer")),
                new Div(
                    new Button(new Text("speech.model.install")).Attribute("type", "button").Attribute("data-speech-install", "").Attribute("hidden", "hidden"),
                    new Button(new Text("speech.model.remove")).Attribute("type", "button").Attribute("data-speech-remove", "").Attribute("hidden", "hidden")
                ).Class("speech-settings__actions"),
                new P(new A(new Text("speech.runtimeLicenses")).Attribute("href", "https://github.com/Juloc/Jularr/blob/main/docs/SPEECH.md#browser-runtime-licenses")
                    .Attribute("target", "_blank").Attribute("rel", "noopener noreferrer"),
                    new A(new Text("speech.runtimeNotices")).Attribute("data-speech-runtime-notices", "")
                        .Attribute("href", "https://github.com/Juloc/Jularr/blob/main/src/Jularr.Frontend/Modules/Speech/Runtime/NOTICE.md")
                        .Attribute("target", "_blank").Attribute("rel", "noopener noreferrer")).Class("speech-settings__licenses")
            ).Class("speech-settings").Attribute("data-speech-settings", "").Attribute("data-speech-language", language)
                .Attribute("data-speech-preferences", JsonSerializer.Serialize(preferences, Json))
                .Attribute("data-speech-models", JsonSerializer.Serialize(available, Json))
                .Attribute("data-speech-default-label", texts["speech.voice.default"]).Attribute("data-speech-missing-label", texts["speech.voice.missing"])
                .TextAttribute("data-speech-voice-choose", "speech.voice.choose")
                .TextAttribute("data-speech-model-choose", "speech.model.choose").TextAttribute("data-speech-model-installed", "speech.model.installed")
                .TextAttribute("data-speech-model-missing", "speech.model.notInstalled").TextAttribute("data-speech-model-busy", "speech.model.installing")
                .TextAttribute("data-speech-model-failed", "speech.model.failed").TextAttribute("data-speech-model-removed", "speech.model.removed")
                .TextAttribute("data-speech-storage-failed", "speech.model.storageFailed")
                .WriteTo(writer, encoder, texts);
        }
    }
}
