using Jularr.Integrations.Logic.Media;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jularr.Backend.Service.Core
{
    // Runs `cjxl --version` once at start and says in the log whether the cover cache will hold JPEG XL; without it the cache keeps WebP.
    internal sealed class JxlStartupCheck(JxlEncoder encoder, ILogger<JxlStartupCheck> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(encoder.ExecutablePath))
            {
                logger.LogInformation("cjxl is not configured (Media:CjxlPath); covers are kept as WebP.");
            }
            else if (await encoder.DetectAsync(cancellationToken))
            {
                logger.LogInformation("cjxl found ({Version}); covers are kept as JPEG XL.", encoder.Version);
            }
            else
            {
                logger.LogWarning("cjxl at {Path} did not run; covers are kept as WebP.", encoder.ExecutablePath);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
