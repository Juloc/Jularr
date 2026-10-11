using System.Collections.Frozen;

namespace Jularr.Backend.Data.Common
{
    public sealed class UiTextBundle
    {
        private readonly FrozenDictionary<string, string> _texts;

        internal UiTextBundle(long uiLocaleId, string locale, bool isRightToLeft, string version,
            IReadOnlyDictionary<string, string> texts)
        {
            UiLocaleId = uiLocaleId;
            Locale = locale;
            IsRightToLeft = isRightToLeft;
            Version = version;
            _texts = texts.ToFrozenDictionary(StringComparer.Ordinal);
        }

        public long UiLocaleId { get; }
        public string Locale { get; }
        public bool IsRightToLeft { get; }

        // Includes each fallback layer's revision; clients must treat it as opaque.
        public string Version { get; }
        public IReadOnlyDictionary<string, string> Texts => _texts;
        public string this[string key] => _texts.GetValueOrDefault(key, key);
    }
}
