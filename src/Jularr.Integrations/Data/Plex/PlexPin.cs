using System.Text.Json.Serialization;

namespace Jularr.Integrations.Data.Plex
{
    public sealed class PlexPin
    {
        public PlexPin(long id, string code, byte[] privateKey)
        {
            Id = id;
            Code = code;
            PrivateKey = privateKey;
        }

        public long Id { get; }
        public string Code { get; }

        [JsonIgnore]
        public byte[] PrivateKey { get; }
    }
}
