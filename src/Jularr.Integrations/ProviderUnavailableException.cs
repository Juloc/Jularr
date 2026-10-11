namespace Jularr.Integrations
{
    public sealed class ProviderUnavailableException : Exception
    {
        public ProviderUnavailableException() : base("The external provider is unavailable.")
        {
        }
    }
}
