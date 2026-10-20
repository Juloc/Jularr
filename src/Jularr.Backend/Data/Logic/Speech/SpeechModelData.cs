using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Speech;

namespace Jularr.Backend.Data.Logic.Speech
{
    internal sealed class SpeechModelHashesData(string[] hashes) : BaseData
    {
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Hashes))]
        public string[] Hashes { get; } = hashes;
    }

    internal sealed class SpeechModelData(string hash, bool isEnabled, long version) : BaseData
    {
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Hash))]
        public string Hash { get; } = hash;
        [DataField(typeof(SpeechFields), nameof(SpeechFields.IsEnabled))]
        public bool IsEnabled { get; } = isEnabled;
        [DataField(typeof(SpeechFields), nameof(SpeechFields.Version))]
        public long Version { get; } = version;
    }
}
