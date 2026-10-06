using Jularr.Web.Features.Learning.LanguageAssistance;
using Jularr.Web.Features.Playback;
using Jularr.Web.Features.Vocabulary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jularr.Tests;

/// <summary>
/// A host without the MeCab dictionary (a Windows development machine, a broken image) must still resolve every
/// service that depends on Japanese analysis; only the analysis itself reports unavailable.
/// </summary>
[TestClass]
public sealed class JapaneseMorphologyAvailabilityTests
{
    private static readonly string MissingDictionary = Path.Combine(Path.GetTempPath(), $"jularr-no-mecab-{Guid.NewGuid():N}");

    [TestMethod]
    public void ServicesDependingOnTheDictionaryResolveWithoutIt()
    {
        using var provider = BuildProvider(new RecordingLoggerProvider());

        Assert.IsNotNull(provider.GetRequiredService<IJapaneseMorphology>());
        Assert.IsNotNull(provider.GetRequiredService<JapaneseTermExtractor>());
        Assert.IsNotNull(provider.GetRequiredService<LanguageTextAnalyzer>());
        Assert.IsNotNull(provider.GetRequiredService<PlaybackCueProjector>());
    }

    [TestMethod]
    public void TheOutageIsReportedPerCallAndTheRootCauseIsLoggedOnce()
    {
        var logs = new RecordingLoggerProvider();
        using var provider = BuildProvider(logs);
        var morphology = provider.GetRequiredService<IJapaneseMorphology>();

        Assert.IsFalse(morphology.Status.IsAvailable);
        Assert.AreEqual(MissingDictionary, morphology.Status.DictionaryPath);

        var first = Assert.ThrowsExactly<JapaneseAnalysisUnavailableException>(() => morphology.Analyze("日本語"));
        Assert.IsInstanceOfType<DirectoryNotFoundException>(first.InnerException);
        Assert.ThrowsExactly<JapaneseAnalysisUnavailableException>(() => morphology.Analyze("もう一度"));

        var errors = logs.Entries.Where(x => x.Level == LogLevel.Error).ToList();
        Assert.AreEqual(1, errors.Count, "The failure is recorded when the dictionary is first needed, not for every call.");
        Assert.IsInstanceOfType<DirectoryNotFoundException>(errors[0].Exception);
    }

    [TestMethod]
    public void PlaybackAndReadingKeepWorkingWithoutAnnotations()
    {
        using var provider = BuildProvider(new RecordingLoggerProvider());

        var cue = provider.GetRequiredService<PlaybackCueProjector>().Project(0, 1000, "日本語の字幕", new Dictionary<string, PlaybackTermInfo>());
        Assert.AreEqual("日本語の字幕", string.Concat(cue.Tokens.Select(x => x.Surface)));
        Assert.IsTrue(cue.Tokens.All(x => x.TermId is null));

        var analyzer = provider.GetRequiredService<LanguageTextAnalyzer>();
        Assert.IsFalse(analyzer.CanAnalyze("ja"));
        Assert.IsTrue(analyzer.CanAnalyze("en"), "Languages without readings never depend on the dictionary.");
        var tokens = analyzer.Analyze("日本語の文", "ja");
        Assert.AreEqual("日本語の文", string.Concat(tokens.Select(x => x.Surface)));
        Assert.IsTrue(tokens.All(x => !x.Interactive));
    }

    private static ServiceProvider BuildProvider(RecordingLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.Configure<JapaneseMorphologyOptions>(options => options.DictionaryPath = MissingDictionary);
        services.AddSingleton<IJapaneseMorphology, MeCabJapaneseMorphology>();
        services.AddSingleton<JapaneseTermExtractor>();
        services.AddSingleton<JapaneseDictionary>();
        services.AddSingleton<LanguageTextAnalyzer>();
        services.AddSingleton<PlaybackCueProjector>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(Entries);

        public void Dispose()
        {
        }

        private sealed class Recorder(List<(LogLevel Level, Exception? Exception)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, exception));
        }
    }
}
