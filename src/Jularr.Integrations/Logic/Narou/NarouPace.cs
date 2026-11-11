using Jularr.Integrations.Logic.Common;

namespace Jularr.Integrations.Logic.Narou
{
    // Narō's two hosts, the novel API and the site with the chapters, each with its own pace: requests of one host start at least the interval apart.
    internal sealed class NarouPace(TimeSpan interval)
    {
        public RequestPace<NarouClient> Api { get; } = new(interval);
        public RequestPace<NarouClient> Site { get; } = new(interval);
    }
}
