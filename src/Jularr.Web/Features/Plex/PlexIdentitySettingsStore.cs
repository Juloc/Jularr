using System.Text.Json;
using Jularr.Web.Features.Providers;

namespace Jularr.Web.Features.Plex;

public sealed record PlexIdentitySettings(
    bool Enabled,
    bool LoginEnabled,
    bool LinkEnabled,
    bool AutoProvisionEnabled,
    bool RequireApproval,
    string ClientIdentifier)
{
    public bool CanLogin => Enabled && LoginEnabled && ClientIdentifier.Length > 0;
    public bool CanLink => Enabled && LinkEnabled && ClientIdentifier.Length > 0;
}

public sealed class PlexIdentitySettingsStore(
    IConfiguration configuration,
    string? directory = null)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(
        directory ?? "/data/integrations", "plex-identity.json");
    private PlexIdentitySettings? cached;

    public bool ExternallyManaged =>
        configuration.GetSection("Plex").GetChildren().Any();

    public async Task<PlexIdentitySettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        if (ExternallyManaged)
        {
            var login = configuration.GetValue("Plex:LoginEnabled", false);
            var link = configuration.GetValue("Plex:LinkEnabled", false);
            return new PlexIdentitySettings(
                login || link,
                login,
                link,
                configuration.GetValue("Plex:AutoProvisionEnabled", false),
                configuration.GetValue("Plex:RequireApproval", true),
                configuration["Plex:ClientIdentifier"]?.Trim() ?? string.Empty);
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is not null)
            {
                return cached;
            }

            if (!File.Exists(path))
            {
                return new PlexIdentitySettings(
                    false, false, false, false, true, string.Empty);
            }

            var data = await File.ReadAllTextAsync(path, cancellationToken);
            cached = JsonSerializer.Deserialize<PlexIdentitySettings>(
                data, JsonOptions) ?? throw new InvalidDataException(
                "Plex identity settings are empty.");
            if (cached.ClientIdentifier.Length > 120 ||
                cached.ClientIdentifier.Any(c => !char.IsAsciiLetterOrDigit(c)
                    && c is not ('-' or '_' or '.')))
            {
                throw new InvalidDataException(
                    "Plex identity settings contain an invalid client identifier.");
            }

            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        bool enabled,
        bool loginEnabled,
        bool linkEnabled,
        bool autoProvisionEnabled,
        bool requireApproval,
        CancellationToken cancellationToken)
    {
        if (ExternallyManaged)
        {
            throw new InvalidOperationException(
                "Plex identity settings are managed by deployment configuration.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = cached;
            if (current is null && File.Exists(path))
            {
                current = JsonSerializer.Deserialize<PlexIdentitySettings>(
                    await File.ReadAllTextAsync(path, cancellationToken),
                    JsonOptions);
            }

            var identifier = string.IsNullOrWhiteSpace(current?.ClientIdentifier)
                ? Guid.NewGuid().ToString("N")
                : current.ClientIdentifier;

            var saved = new PlexIdentitySettings(
                enabled,
                loginEnabled,
                linkEnabled,
                autoProvisionEnabled,
                requireApproval,
                identifier);

            await ProviderCredentialFile.WriteAtomicAsync(
                path,
                JsonSerializer.Serialize(saved, JsonOptions),
                cancellationToken);
            cached = saved;
        }
        finally
        {
            gate.Release();
        }
    }
}
