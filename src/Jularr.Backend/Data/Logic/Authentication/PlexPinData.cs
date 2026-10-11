using System.Text.Json.Serialization;

namespace Jularr.Backend.Data.Logic.Authentication
{
    public sealed class PlexPinData
    {
        public PlexPinData(long id, string code, byte[] privateKey)
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
