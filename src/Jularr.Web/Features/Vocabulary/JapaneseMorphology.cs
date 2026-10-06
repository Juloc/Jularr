using System.Text;
using DotNetG2P;
using DotNetG2P.MeCab;
using Microsoft.Extensions.Options;

namespace Jularr.Web.Features.Vocabulary;

public sealed record JapaneseMorphToken(
    string Surface,
    string Canonical,
    string Reading,
    string PartOfSpeech);

/// <summary>Whether Japanese text analysis can run, with the admin-facing reason when it cannot.</summary>
public sealed record JapaneseMorphologyStatus(bool IsAvailable, string? DictionaryPath, string? Problem)
{
    public static JapaneseMorphologyStatus Ready { get; } = new(true, null, null);
}

/// <summary>Raised by <see cref="IJapaneseMorphology.Analyze"/> when the dictionary could not be loaded; the inner exception is the root cause.</summary>
public sealed class JapaneseAnalysisUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class JapaneseMorphologyOptions
{
    public const string SectionName = "JapaneseMorphology";

    /// <summary>The location the container image installs the MeCab (NAIST-jdic) dictionary to; other hosts override it.</summary>
    public const string ContainerDictionaryPath = "/var/lib/mecab/dic/open-jtalk/naist-jdic";

    public string DictionaryPath { get; set; } = ContainerDictionaryPath;
}

public interface IJapaneseMorphology
{
    /// <summary>Resolved on first use; implementations that cannot fail are always ready.</summary>
    JapaneseMorphologyStatus Status => JapaneseMorphologyStatus.Ready;

    /// <exception cref="JapaneseAnalysisUnavailableException">The dictionary is not loaded; check <see cref="Status"/> first on paths that can degrade.</exception>
    IReadOnlyList<JapaneseMorphToken> Analyze(string text);
}

/// <summary>
/// MeCab-backed analysis. The dictionary is loaded on first use instead of at service construction so a missing dictionary
/// disables only Japanese analysis; the outcome (including a failure) is kept until the process restarts.
/// </summary>
public sealed class MeCabJapaneseMorphology : IJapaneseMorphology, IDisposable
{
    private readonly Lazy<Loaded> loaded;
    private readonly object gate = new();

    public MeCabJapaneseMorphology(IOptions<JapaneseMorphologyOptions> options, ILogger<MeCabJapaneseMorphology> logger)
    {
        var dictionaryPath = options.Value.DictionaryPath;
        loaded = new Lazy<Loaded>(() => Load(dictionaryPath, logger), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public JapaneseMorphologyStatus Status => loaded.Value.Status;

    public IReadOnlyList<JapaneseMorphToken> Analyze(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var current = loaded.Value;
        if (current.Tokenizer is not { } tokenizer)
        {
            throw new JapaneseAnalysisUnavailableException(current.Status.Problem!, current.Failure);
        }

        var normalized = text.Normalize(NormalizationForm.FormKC);

        lock (gate)
        {
            return tokenizer.Tokenize(normalized)
                .Select(token =>
                {
                    var canonical = token.OriginalForm is "*" or ""
                        ? token.Surface
                        : token.OriginalForm;
                    var reading = token.Reading is "*" or ""
                        ? token.Surface
                        : token.Reading;

                    return new JapaneseMorphToken(
                        token.Surface,
                        canonical,
                        reading,
                        token.POS);
                })
                .ToArray();
        }
    }

    public void Dispose()
    {
        if (loaded.IsValueCreated)
        {
            loaded.Value.Tokenizer?.Dispose();
        }
    }

    private static Loaded Load(string dictionaryPath, ILogger logger)
    {
        try
        {
            if (!File.Exists(Path.Combine(dictionaryPath, "sys.dic")))
            {
                throw new DirectoryNotFoundException($"No MeCab dictionary (sys.dic) was found in '{dictionaryPath}'.");
            }

            return new Loaded(new MeCabTokenizer(dictionaryPath), new JapaneseMorphologyStatus(true, dictionaryPath, null), null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogError(
                exception,
                "The MeCab dictionary at {DictionaryPath} could not be loaded; Japanese text analysis is unavailable until the server is restarted with a valid {OptionName}.",
                dictionaryPath,
                $"{JapaneseMorphologyOptions.SectionName}:{nameof(JapaneseMorphologyOptions.DictionaryPath)}");

            var problem = $"The MeCab dictionary could not be loaded from '{dictionaryPath}'.";
            return new Loaded(null, new JapaneseMorphologyStatus(false, dictionaryPath, problem), exception);
        }
    }

    private sealed record Loaded(MeCabTokenizer? Tokenizer, JapaneseMorphologyStatus Status, Exception? Failure);
}
