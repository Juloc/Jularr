using System.Collections.Concurrent;
using Jularr.Backend.Data.Common;
using Jularr.Integrations.Logic.Ocr;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jularr.Backend.Service.Core
{
    // Reads the text of scanned pages (the picture and the degrees clockwise the page shows it turned); the models come from the OCR folder.
    internal interface IPageRecognizer
    {
        Task<Func<byte[], int, OcrPage>> RecognizerAsync(string? language, CancellationToken ct);
    }

    // The PP-OCRv5 models in the OCR folder, each fetched the first time a scanned book needs it; one engine per script stays loaded.
    internal sealed class OcrFiles(IServiceProvider services, IOptions<MediaOptions> options) : IPageRecognizer, IDisposable
    {
        private static readonly SemaphoreSlim Downloads = new(1, 1);
        private readonly ConcurrentDictionary<string, OcrEngine> _engines = new(StringComparer.Ordinal);

        public async Task<Func<byte[], int, OcrPage>> RecognizerAsync(string? language, CancellationToken ct)
        {
            var root = options.Value.OcrPath;
            var recognition = OcrModels.Recognition(language);
            if (_engines.TryGetValue(recognition.FileName, out var loaded))
            {
                return loaded.Read;
            }

            await Downloads.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(root);
                foreach (var model in new[] { OcrModels.Detection, recognition })
                {
                    var path = Path.Combine(root, model.FileName);
                    if (!File.Exists(path))
                    {
                        await services.GetRequiredService<OcrModelClient>().DownloadAsync(model, path, ct);
                    }
                }

                return _engines.GetOrAdd(recognition.FileName, _ => new OcrEngine(Path.Combine(root, OcrModels.Detection.FileName), Path.Combine(root, recognition.FileName))).Read;
            }
            catch (OcrSourceException exception)
            {
                throw new ServiceUnavailableException("ocr_unavailable", exception);
            }
            finally
            {
                Downloads.Release();
            }
        }

        public void Dispose()
        {
            foreach (var engine in _engines.Values)
            {
                engine.Dispose();
            }
        }
    }
}
