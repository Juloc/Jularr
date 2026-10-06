using System.Security.Cryptography;
using System.Text.Json;
using Jularr.Web.Features.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Ai;

public sealed class AiProfileSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDataProtector protector;
    private readonly ILogger<AiProfileSettingsStore> logger;
    private readonly string accountDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public AiProfileSettingsStore(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AiProfileSettingsStore> logger)
        : this(
            dataProtectionProvider,
            logger,
            new DirectoryInfo("/data/integrations"))
    {
    }

    public AiProfileSettingsStore(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AiProfileSettingsStore> logger,
        DirectoryInfo integrationDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(integrationDirectory);

        protector = dataProtectionProvider.CreateProtector(
            "Jularr.Ai.ProfileApiKey.v1");
        this.logger = logger;
        accountDirectory = Path.Combine(
            integrationDirectory.FullName,
            "ai",
            "accounts");
    }

    public async Task<AiProfileSettings> LoadAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var safeProfileId = ValidateProfileId(profileId);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = GetPath(safeProfileId);
            if (!File.Exists(path))
            {
                return AiProfileSettings.Default;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                var persisted = JsonSerializer.Deserialize<PersistedAiProfileSettings>(
                    json,
                    JsonOptions);

                if (persisted is null || !AiProviderIds.IsSupported(persisted.ProviderId))
                {
                    return AiProfileSettings.Default;
                }

                var apiKey = string.IsNullOrWhiteSpace(persisted.ProtectedApiKey)
                    ? null
                    : protector.Unprotect(persisted.ProtectedApiKey);

                return Validate(
                    new AiProfileSettings(
                        persisted.ProviderId,
                        persisted.BaseUrl,
                        persisted.Model,
                        apiKey,
                        persisted.TranslationMode)
                    {
                        ImageModel = persisted.ImageModel,
                        ReasoningEffort = persisted.ReasoningEffort,
                        ServiceTier = persisted.ServiceTier,
                        MaxOutputTokens = persisted.MaxOutputTokens,
                        DailyTokenBudget = persisted.DailyTokenBudget,
                        BudgetWarningPercent = persisted.BudgetWarningPercent,
                        MaxConcurrentJobs = persisted.MaxConcurrentJobs,
                        SessionTokenBudget = persisted.SessionTokenBudget,
                        ContextBudgetTokens = persisted.ContextBudgetTokens,
                        MaxRetries = persisted.MaxRetries,
                        TimeoutSeconds = persisted.TimeoutSeconds,
                        Verbosity = persisted.Verbosity,
                        FallbackModelEnabled = persisted.FallbackModelEnabled,
                        Overrides = AiOperationOverrides.From(
                            persisted.Overrides?
                                .Select(x => KeyValuePair.Create(
                                    x.Key,
                                    new AiOperationOverride(x.Value.Model, x.Value.ReasoningEffort)
                                    {
                                        ContextBudgetTokens = x.Value.ContextBudgetTokens,
                                        MaxOutputTokens = x.Value.MaxOutputTokens,
                                        MaxRetries = x.Value.MaxRetries,
                                        TimeoutSeconds = x.Value.TimeoutSeconds,
                                        Verbosity = x.Value.Verbosity
                                    }))
                            ?? [])
                    },
                    requireSecret: false);
            }
            catch (Exception exception) when (
                exception is JsonException
                    or CryptographicException
                    or IOException
                    or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    exception,
                    "Could not load AI settings for profile {ProfileId}.",
                    safeProfileId);
                return AiProfileSettings.Default;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        string profileId,
        AiProfileSettings settings,
        CancellationToken cancellationToken = default)
    {
        var safeProfileId = ValidateProfileId(profileId);
        var validated = Validate(settings, requireSecret: true);

        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(accountDirectory);
            var path = GetPath(safeProfileId);
            var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";

            var persisted = new PersistedAiProfileSettings(
                validated.ProviderId,
                validated.BaseUrl,
                validated.Model,
                string.IsNullOrWhiteSpace(validated.ApiKey)
                    ? null
                    : protector.Protect(validated.ApiKey),
                validated.TranslationMode,
                validated.ImageModel,
                validated.ReasoningEffort,
                validated.ServiceTier,
                validated.MaxOutputTokens,
                validated.Overrides.Count == 0
                    ? null
                    : validated.Overrides.Items.ToDictionary(
                        x => x.Key,
                        x => new PersistedAiOperationOverride(x.Value.Model, x.Value.ReasoningEffort)
                        {
                            ContextBudgetTokens = x.Value.ContextBudgetTokens,
                            MaxOutputTokens = x.Value.MaxOutputTokens,
                            MaxRetries = x.Value.MaxRetries,
                            TimeoutSeconds = x.Value.TimeoutSeconds,
                            Verbosity = x.Value.Verbosity
                        },
                        StringComparer.Ordinal))
            {
                DailyTokenBudget = validated.DailyTokenBudget,
                BudgetWarningPercent = validated.BudgetWarningPercent,
                MaxConcurrentJobs = validated.MaxConcurrentJobs,
                SessionTokenBudget = validated.SessionTokenBudget,
                ContextBudgetTokens = validated.ContextBudgetTokens,
                MaxRetries = validated.MaxRetries,
                TimeoutSeconds = validated.TimeoutSeconds,
                Verbosity = validated.Verbosity,
                FallbackModelEnabled = validated.FallbackModelEnabled
            };

            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(persisted, JsonOptions),
                    cancellationToken);
                SetPrivateFileMode(temporaryPath);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                exception,
                "Could not save AI settings for profile {ProfileId}.",
                safeProfileId);
            throw new InvalidOperationException(
                "AI settings could not be saved to persistent storage.",
                exception);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ResetAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var safeProfileId = ValidateProfileId(profileId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            TryDelete(GetPath(safeProfileId));
        }
        finally
        {
            gate.Release();
        }
    }

    private string GetPath(string profileId) =>
        Path.Combine(accountDirectory, $"{profileId}.json");

    private static AiProfileSettings Validate(
        AiProfileSettings settings,
        bool requireSecret)
    {
        if (!AiProviderIds.IsSupported(settings.ProviderId))
        {
            throw new InvalidOperationException("Unsupported AI provider.");
        }

        if (!Enum.IsDefined(settings.TranslationMode))
        {
            throw new InvalidOperationException("Unsupported AI translation mode.");
        }

        var effort = CleanOption(settings.ReasoningEffort, "reasoning effort");
        var serviceTier = CleanOption(settings.ServiceTier, "service tier");
        if (settings.MaxOutputTokens is < 1 or > AiProfileSettings.MaxOutputTokensLimit)
        {
            throw new InvalidOperationException(
                $"The output token limit must be between 1 and {AiProfileSettings.MaxOutputTokensLimit}.");
        }

        if (settings.DailyTokenBudget is < 1 or > AiProfileSettings.MaxDailyTokenBudget)
        {
            throw new InvalidOperationException(
                $"The daily token limit must be between 1 and {AiProfileSettings.MaxDailyTokenBudget}.");
        }

        if (settings.BudgetWarningPercent is < 1 or > 99)
        {
            throw new InvalidOperationException("The warning threshold must be between 1 and 99 percent.");
        }

        if (settings.MaxConcurrentJobs is < 1 or > AiProfileSettings.MaxConcurrentJobsLimit)
        {
            throw new InvalidOperationException(
                $"Parallel AI tasks must be between 1 and {AiProfileSettings.MaxConcurrentJobsLimit}.");
        }

        if (settings.SessionTokenBudget is < 1 or > AiProfileSettings.MaxSessionTokenBudget)
        {
            throw new InvalidOperationException(
                $"The session token limit must be between 1 and {AiProfileSettings.MaxSessionTokenBudget}.");
        }

        if (settings.ContextBudgetTokens is < 1 or > AiProfileSettings.MaxContextBudgetTokens)
        {
            throw new InvalidOperationException(
                $"The context budget must be between 1 and {AiProfileSettings.MaxContextBudgetTokens} tokens.");
        }

        if (settings.MaxRetries is < 0 or > AiProfileSettings.MaxRetriesLimit)
        {
            throw new InvalidOperationException(
                $"Retries must be between 0 and {AiProfileSettings.MaxRetriesLimit}.");
        }

        if (settings.TimeoutSeconds is < AiProfileSettings.MinTimeoutSeconds or > AiProfileSettings.MaxTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"The timeout must be between {AiProfileSettings.MinTimeoutSeconds} and {AiProfileSettings.MaxTimeoutSeconds} seconds.");
        }

        var verbosity = CleanOption(settings.Verbosity, "verbosity");

        foreach (var (operation, value) in settings.Overrides.Items)
        {
            if ((value.Model is not null && !AiProfileSettings.IsValidModelId(value.Model))
                || (value.ReasoningEffort is not null && !AiProfileSettings.IsValidOptionId(value.ReasoningEffort))
                || (value.Verbosity is not null && !AiProfileSettings.IsValidOptionId(value.Verbosity))
                || value.ContextBudgetTokens is < 1 or > AiProfileSettings.MaxContextBudgetTokens
                || value.MaxOutputTokens is < 1 or > AiProfileSettings.MaxOutputTokensLimit
                || value.MaxRetries is < 0 or > AiProfileSettings.MaxRetriesLimit
                || value.TimeoutSeconds is < AiProfileSettings.MinTimeoutSeconds or > AiProfileSettings.MaxTimeoutSeconds)
            {
                throw new InvalidOperationException($"The override for {operation} is not valid.");
            }
        }

        if (settings.ProviderId == AiProviderIds.Server)
        {
            // The server's Codex connection is shared: a profile may pick a catalog model and
            // reasoning effort, but never an endpoint, key or output limit. Per-task output-token
            // caps are OpenAI-compatible only, so they are stripped here too.
            var serverModel = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim();
            if (serverModel is not null && !AiProfileSettings.IsValidModelId(serverModel))
            {
                throw new InvalidOperationException("Enter a valid model name.");
            }

            var serverEntries = settings.Overrides.Items
                .Select(x => KeyValuePair.Create(x.Key, x.Value with { MaxOutputTokens = null }));

            return settings with
            {
                BaseUrl = null,
                Model = serverModel,
                ApiKey = null,
                ImageModel = null,
                ReasoningEffort = effort,
                ServiceTier = serviceTier,
                MaxOutputTokens = null,
                Verbosity = verbosity,
                Overrides = AiOperationOverrides.From(
                    serverEntries,
                    serverModel,
                    effort,
                    settings.ContextBudgetTokens,
                    null,
                    settings.MaxRetries,
                    settings.TimeoutSeconds,
                    verbosity)
            };
        }

        var imageModel = settings.ImageModel?.Trim();
        if (imageModel?.Length > 120)
        {
            throw new InvalidOperationException(
                "Enter a valid image model name.");
        }

        var baseUrl = settings.BaseUrl?.Trim();
        // The model may stay empty until the provider's models were loaded; AI work waits for it.
        var model = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim();
        var apiKey = settings.ApiKey?.Trim();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                "Enter a valid HTTP(S) base URL for the OpenAI-compatible provider.");
        }

        if (model is not null && !AiProfileSettings.IsValidModelId(model))
        {
            throw new InvalidOperationException(
                "Enter a valid model name.");
        }

        if (requireSecret && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "An API key is required for the OpenAI-compatible provider.");
        }

        // Verbosity is a Codex response setting; personal OpenAI-compatible providers never receive it.
        var personalEntries = settings.Overrides.Items
            .Select(x => KeyValuePair.Create(x.Key, x.Value with { Verbosity = null }));

        return settings with
        {
            BaseUrl = baseUrl.TrimEnd('/'),
            Model = model,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            ImageModel = string.IsNullOrWhiteSpace(imageModel) ? null : imageModel,
            ReasoningEffort = effort,
            ServiceTier = serviceTier,
            Verbosity = null,
            Overrides = AiOperationOverrides.From(
                personalEntries,
                model,
                effort,
                settings.ContextBudgetTokens,
                settings.MaxOutputTokens,
                settings.MaxRetries,
                settings.TimeoutSeconds)
        };
    }

    private static string? CleanOption(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clean = value.Trim().ToLowerInvariant();
        return AiProfileSettings.IsValidOptionId(clean)
            ? clean
            : throw new InvalidOperationException($"Unsupported {name}.");
    }

    private static string ValidateProfileId(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        if (profileId.Length > 80 ||
            profileId.Any(character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Profile ID contains unsupported characters.",
                nameof(profileId));
        }

        return profileId;
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PersistedAiProfileSettings(
        string ProviderId,
        string? BaseUrl,
        string? Model,
        string? ProtectedApiKey,
        AiTranslationMode TranslationMode,
        string? ImageModel = null,
        string? ReasoningEffort = null,
        string? ServiceTier = null,
        int? MaxOutputTokens = null,
        Dictionary<string, PersistedAiOperationOverride>? Overrides = null)
    {
        public int? DailyTokenBudget { get; init; }

        public int? BudgetWarningPercent { get; init; }

        public int? MaxConcurrentJobs { get; init; }

        public int? SessionTokenBudget { get; init; }

        public int? ContextBudgetTokens { get; init; }

        public int? MaxRetries { get; init; }

        public int? TimeoutSeconds { get; init; }

        public string? Verbosity { get; init; }

        public bool FallbackModelEnabled { get; init; }
    }

    private sealed record PersistedAiOperationOverride(
        string? Model,
        string? ReasoningEffort)
    {
        public int? ContextBudgetTokens { get; init; }

        public int? MaxOutputTokens { get; init; }

        public int? MaxRetries { get; init; }

        public int? TimeoutSeconds { get; init; }

        public string? Verbosity { get; init; }
    }
}
