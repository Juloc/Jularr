using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

namespace Jularr.Frontend.Framework
{
    // Pages, fragments, API answers, styles and scripts leave compressed (Brotli, else gzip) at the fastest level: the HTML of a list or a shelf
    // shrinks to a fraction on the wire. Images, video, audio and the live event stream are sent as they are. Antiforgery tokens are encrypted
    // per response, so compressed pages give away no stable secret.
    public static class Compression
    {
        public static IServiceCollection AddJularrCompression(this IServiceCollection services)
        {
            services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml", "application/manifest+json", "application/problem+json"]);
            });
            services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
            services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
            return services;
        }
    }
}
