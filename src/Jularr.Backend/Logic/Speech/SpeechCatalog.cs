using System.Text.Json;
using System.Text.RegularExpressions;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Speech;
using Jularr.Backend.Data.Enums.Speech;

namespace Jularr.Backend.Logic.Speech
{
    internal sealed record SpeechModel(string Id, string Version, string Name, string Language, string Voice, string License, Uri LicenseUrl, Uri Source, long SizeBytes, string Sha256,
        bool RequiresLicenseAcceptance = false, Uri? SampleUrl = null, SpeechVoiceGenderType Gender = SpeechVoiceGenderType.Unknown, Uri? GenderSource = null);

    internal static partial class SpeechCatalog
    {
        public const int MaximumEntries = 1024;
        public const int MaximumCatalogBytes = 2_097_152;

        [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z")]
        private static partial Regex Identifier();

        [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
        internal static partial Regex Hash();

        // An invalid entry never replaces a verified one or becomes an install option.
        internal static IReadOnlyList<SpeechModel> Parse(string json)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumCatalogBytes) throw new InvalidDataException("The speech catalog is too large.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > MaximumEntries) throw new InvalidDataException("Invalid speech catalog.");

            var result = new List<SpeechModel>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in models.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "id") is not { } id || !Identifier().IsMatch(id)
                    || Text(item, "version") is not { } version || !Identifier().IsMatch(version)
                    || Text(item, "name") is not { Length: <= 128 } name || Text(item, "language") is not { Length: <= 64 } language
                    || Text(item, "voice") is not { } voice || !Identifier().IsMatch(voice)
                    || Text(item, "license") is not { Length: <= 128 } license || !Address(item, "licenseUrl", out var licenseUrl)
                    || !Address(item, "source", out var source) || Text(item, "sha256") is not { } hash || !Hash().IsMatch(hash)
                    || !item.TryGetProperty("sizeBytes", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes)
                    || bytes is < 1 or > 536_870_912) continue;

                try
                {
                    language = (string)SpeechFields.Language.Normalize(language)!;
                    SpeechFields.Language.Validate(language, nameof(language));
                }
                catch (DataValidationException) { continue; }
                if (language.Length == 0 || language == "und" || identities.Contains(id) || hashes.Contains(hash)) continue;

                var hasAcceptance = item.TryGetProperty("requiresLicenseAcceptance", out var acceptance);
                if (hasAcceptance && acceptance.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                var requiresAcceptance = hasAcceptance && acceptance.ValueKind == JsonValueKind.True;
                Uri? sampleUrl = null;
                if (item.TryGetProperty("sampleUrl", out var sample) && sample.ValueKind != JsonValueKind.Null)
                {
                    if (!Address(item, "sampleUrl", out sampleUrl) || sampleUrl!.Host != "k2-fsa.github.io" || !sampleUrl.IsDefaultPort || sampleUrl.Query.Length != 0
                        || !sampleUrl.AbsolutePath.StartsWith("/sherpa/onnx/tts/all/piper/mp3/", StringComparison.Ordinal)) continue;
                }

                var gender = SpeechVoiceGenderType.Unknown;
                Uri? genderSource = null;
                if (item.TryGetProperty("gender", out var classification))
                {
                    if (classification.ValueKind != JsonValueKind.Number || !classification.TryGetByte(out var number)
                        || !Enum.IsDefined((SpeechVoiceGenderType)number)) continue;
                    gender = (SpeechVoiceGenderType)number;
                    if (gender != SpeechVoiceGenderType.Unknown && !Address(item, "genderSource", out genderSource)) continue;
                }

                identities.Add(id);
                hashes.Add(hash);
                result.Add(new SpeechModel(id, version, name, language, voice, license, licenseUrl!, source!, bytes, hash, requiresAcceptance, sampleUrl, gender, genderSource));
            }

            return result;
        }

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;

        private static bool Address(JsonElement element, string name, out Uri? address) =>
            Uri.TryCreate(Text(element, name), UriKind.Absolute, out address) && address.AbsoluteUri.Length <= 2048
            && address.Scheme == "https" && address.UserInfo.Length == 0 && address.Fragment.Length == 0;
    }
}
