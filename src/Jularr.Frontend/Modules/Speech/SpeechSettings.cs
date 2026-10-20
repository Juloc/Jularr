using System.Text.Json;
using System.Text.Encodings.Web;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Speech;
using Jularr.Backend.Data.User.Speech.V1;
using Jularr.Backend.Service.Core;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Modules.Speech
{
    // Profile and Reader use the same bound fields; only available device voices are filled locally.
    public sealed class SpeechSettings(SpeechPreferencesV1 preferences, string language, string action, string token, ServiceError? error = null, SaveSpeechPreferencesDataV1? draft = null) : IUiComponent
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
            var providers = new List<SelectOption> { new("1", texts["speech.provider.device"]) };
            if (input.ProviderTypeId == SpeechProviderType.OfflineNeural) providers.Add(new SelectOption("2", texts["speech.provider.neuralUnavailable"]));
            new Div(
                new P(new Text("speech.devicePrivacy")),
                new P(new Text("speech.voicesUnavailable")).Attribute("data-speech-voices-status", "").Attribute("role", "status"),
                new Form<SaveSpeechPreferencesDataV1>(form =>
                {
                    form.Select(item => item.Language, "speech.language", new[] { input.Language, language }.Concat(preferences.Voices.Select(item => item.Language))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Select(code => new SelectOption(code, code)));
                    form.Hidden(item => item.ModelId);
                    form.Hidden(item => item.Version);
                    form.Select(item => item.ProviderTypeId, "speech.provider", providers);
                    form.Select(item => item.VoiceId, "speech.voice", voices);
                    form.Range(item => item.Rate, "speech.rate");
                    form.Range(item => item.Pitch, "speech.pitch");
                    form.Range(item => item.Volume, "speech.volume");
                    form.Save();
                }, action, token, input, error)
            ).Attribute("data-speech-settings", "").Attribute("data-speech-language", language)
                .Attribute("data-speech-preferences", JsonSerializer.Serialize(preferences, Json))
                .Attribute("data-speech-device-label", texts["speech.provider.device"]).Attribute("data-speech-neural-label", texts["speech.provider.neuralUnavailable"])
                .Attribute("data-speech-default-label", texts["speech.voice.default"]).Attribute("data-speech-missing-label", texts["speech.voice.missing"])
                .WriteTo(writer, encoder, texts);
        }
    }
}
