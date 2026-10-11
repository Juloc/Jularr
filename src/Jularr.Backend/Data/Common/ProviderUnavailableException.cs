namespace Jularr.Backend.Data.Common
{
    public sealed class ProviderUnavailableException : Exception
    {
        public ProviderUnavailableException() : base("The external provider is unavailable.")
        {
        }
    }
}
