using System.Net.Http;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Backend.Data.User.Translation.V1;
using Jularr.Backend.Infrastructure.Sql;
using Jularr.Integrations.Logic.Translation;

namespace Jularr.Backend.Service.Core
{
    /// <summary>
    /// A server-side translator. Reader blocks, the book job and subtitles reach it only through <see cref="TranslationProviders"/>, by a model id
    /// "<c>&lt;Key&gt;:&lt;model&gt;</c>". A provider registers once as <c>ITranslationProvider</c> in <c>ServiceApi</c>. Reads (pages, the Reader) use only
    /// what its last <see cref="CheckAsync"/> found (<see cref="Choose"/>, <see cref="Available"/>); a translation about to run uses <see cref="ChooseAsync"/>.
    /// A machine that fails throws <see cref="HttpRequestException"/>, <see cref="System.Text.Json.JsonException"/>, <see cref="InvalidDataException"/> or a
    /// <see cref="TaskCanceledException"/> without the caller's cancellation; the caller then calls <see cref="Fail"/> and chooses again.
    /// </summary>
    internal interface ITranslationProvider
    {
        // The provider part of a model id, lower-case letters, digits and hyphens.
        string Key { get; }

        // The models this provider may translate with, without the provider part; never containing '/'.
        IReadOnlyList<string> Models { get; }

        // Only an administrator may choose its models in the Reader (the Owner's default model translates for everyone).
        bool IsAdminOnly { get; }

        // Asks the provider's machines how they are now and keeps their answers; bounded in time and never part of a page read.
        Task CheckAsync(TranslationSetupV1 setup, CancellationToken ct) => Task.CompletedTask;

        // The models a machine translates with by the last check, without the provider part.
        IReadOnlyList<string> Available(TranslationSetupV1 setup);

        // The machine that translates with this model by the last check, or with the provider's preferred installed one when no model is named (null
        // when the provider only translates when asked by name); null when none answered.
        TranslationMachine? Choose(TranslationSetupV1 setup, string? model);

        // The same for a translation about to run, which may first ask what was never asked.
        Task<TranslationMachine?> ChooseAsync(TranslationSetupV1 setup, string? model, CancellationToken ct);

        // The version of the model on that machine: a translation counts as current only while its digest matches.
        Task<string> DigestAsync(TranslationMachine machine, CancellationToken ct);

        // One block with the passage before it as context; null when no answer passes the provider's checks.
        Task<string?> TranslateBlockAsync(TranslationMachine machine, string source, string target, string text, string? context, CancellationToken ct);

        // A text as the model answers it, without the block checks (numbered subtitle cues).
        Task<string> TranslateAsync(TranslationMachine machine, string source, string target, string text, CancellationToken ct);

        // The machine dropped out: the next choice passes it over for a while.
        void Fail(TranslationMachine machine);

        // Set up with machines of its own, yet none of them answers now (the Reader says so instead of offering nothing).
        bool IsAway(TranslationSetupV1 setup) => false;
    }

    internal sealed record TranslationMachine(ITranslationProvider Provider, string Model, Uri Address)
    {
        public string ModelId => TranslationProviders.Id(Provider.Key, Model);
    }

    // The registered providers by the provider part of a model id.
    internal sealed class TranslationProviders(IEnumerable<ITranslationProvider> providers)
    {
        private readonly ITranslationProvider[] _providers = [.. providers];

        public static string Id(string provider, string model) => provider + ":" + model;

        public static async Task<TranslationSetupV1> SetupAsync(LogicContext ctx, CancellationToken ct) =>
            await ctx.ReadSql.ReadOptionalAsync<TranslationSetupV1>(new SqlStatement
            {
                Statement = """
                    SELECT "TranslationMachines" AS "Machines", "TranslationModel" AS "Model", "CodexEffort" AS "CodexEffort", "CodexFallbackModel" AS "CodexFallbackModel",
                        "CodexDailyTokens" AS "CodexDailyTokens", "CodexRunTokens" AS "CodexRunTokens", "CodexModel" AS "CodexModel" FROM "InstanceSettings" WHERE "Id" = 1;
                    """
            }, ct) ?? new TranslationSetupV1([], null);

        // The provider and model of an id "<provider>:<model>"; null for an unknown provider or a model it does not offer.
        public (ITranslationProvider Provider, string Model)? Find(string? id)
        {
            var separator = id?.IndexOf(':', StringComparison.Ordinal) ?? -1;
            if (id is null || separator <= 0) return null;
            var key = id[..separator];
            var model = id[(separator + 1)..];
            var provider = _providers.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal));
            return provider is not null && provider.Models.Contains(model, StringComparer.Ordinal) ? (provider, model) : null;
        }

        // Every provider asks its machines how they are now (the translationMachines job, Check now on the translation page).
        public async Task CheckAsync(TranslationSetupV1 setup, CancellationToken ct) => await Task.WhenAll(_providers.Select(provider => provider.CheckAsync(setup, ct)));

        // The machine that translates with the named model by the last check; without one the Owner's default model, and without that the first provider's
        // preferred installed model. A named or default model that no machine has gives none: nothing switches to another model by itself.
        public TranslationMachine? Choose(TranslationSetupV1 setup, string? id = null)
        {
            var wanted = id ?? setup.Model;
            if (wanted is not null)
                return Find(wanted) is var (provider, model) ? provider.Choose(setup, model) : null;

            foreach (var provider in _providers)
                if (provider.Choose(setup, null) is { } machine) return machine;
            return null;
        }

        // The same for a translation about to run.
        public async Task<TranslationMachine?> ChooseAsync(TranslationSetupV1 setup, CancellationToken ct, string? id = null)
        {
            var wanted = id ?? setup.Model;
            if (wanted is not null)
                return Find(wanted) is var (provider, model) ? await provider.ChooseAsync(setup, model, ct) : null;

            foreach (var provider in _providers)
                if (await provider.ChooseAsync(setup, null, ct) is { } machine) return machine;
            return null;
        }

        // What translates a book for every reader by default, by the last check: the machine above, never an admin-only provider's model (Codex translates only
        // on a button press, never by itself); Away when nothing translates because a provider set up with machines has none answering.
        public (TranslationMachine? Machine, bool Away) ChooseFor(TranslationSetupV1 setup)
        {
            if (Choose(setup) is { Provider.IsAdminOnly: false } machine) return (machine, false);
            return (null, _providers.Any(provider => provider.IsAway(setup)));
        }

        // The model a holder of the admin-only right translates a chapter with when pressing the button, with what keeps it from doing so now (a limit reached),
        // by the provider's last check; null for anyone else, where no provider translates in several requests, and while the provider is not connected.
        public (string Id, string? Problem)? Offer(TranslationSetupV1 setup, ServiceCaller caller)
        {
            if (!MayUseAdminOnly(caller)) return null;
            foreach (var provider in _providers.OfType<IBatchTranslationProvider>().Where(provider => provider.IsAdminOnly))
            {
                var (model, problem) = provider.Offer(setup);
                if (problem == "not_connected") continue;
                if (model is not null || problem is not null) return (model is null ? "" : Id(provider.Key, model), problem);
            }

            return null;
        }

        // The right that admits an admin-only provider's models, the one the Owner's re-translation by whole passages needs too.
        public const string AdminOnlyRight = "instance.settings.update";

        // Whether the verified caller holds that right.
        public static bool MayUseAdminOnly(ServiceCaller caller) =>
            ServiceGate.GetPermissions(caller.AccountId, caller.ProfileId, caller.ProfileOwnerAccountId, caller.AccountRoleTypeId, caller.IsDevice).Contains(AdminOnlyRight, StringComparer.Ordinal);

        // Whether the caller may choose this model in the Reader: any profile a model of an ordinary provider and the Owner's default model,
        // only a holder of the admin-only right the other models of an admin-only provider.
        public bool MayChoose(string id, TranslationSetupV1 setup, ServiceCaller caller) =>
            Find(id) is var (provider, _) && (!provider.IsAdminOnly || string.Equals(id, setup.Model, StringComparison.Ordinal) || MayUseAdminOnly(caller));

        // The models the caller may choose now by the last check, in the providers' order; an admin-only provider is not even asked for anyone else. Passages: the
        // provider translates a chapter in whole-passage requests (RetranslateLocalReadingSectionV1, the admin-only right), not block by block.
        public ReaderTranslationModelV1[] Available(TranslationSetupV1 setup, ServiceCaller caller)
        {
            var admin = MayUseAdminOnly(caller);
            var models = new List<ReaderTranslationModelV1>();
            foreach (var provider in _providers)
            {
                if (provider.IsAdminOnly && !admin) continue;
                var passages = admin && provider is IBatchTranslationProvider;
                models.AddRange(provider.Available(setup).Select(model => new ReaderTranslationModelV1(Id(provider.Key, model), passages)));
            }
            return [.. models.DistinctBy(model => model.Id, StringComparer.Ordinal)];
        }
    }

    // The Ollama machines of the Owner's order through the same seam; the models and their checks stay those of OllamaLocalModels.
    internal sealed class OllamaTranslationProvider(OllamaLocalModels models, LocalTranslationMachines machines) : ITranslationProvider
    {
        public const string ProviderKey = "ollama";

        public string Key => ProviderKey;
        public IReadOnlyList<string> Models => OllamaLocalModels.Candidates;
        public bool IsAdminOnly => false;

        public Task CheckAsync(TranslationSetupV1 setup, CancellationToken ct) => machines.CheckAsync(setup.Machines, ct);

        public TranslationMachine? Choose(TranslationSetupV1 setup, string? model) =>
            machines.Choose(setup.Machines, model) is var (server, chosen) ? new TranslationMachine(this, chosen, server) : null;

        public async Task<TranslationMachine?> ChooseAsync(TranslationSetupV1 setup, string? model, CancellationToken ct) =>
            await machines.ChooseAsync(setup.Machines, ct, model) is var (server, chosen) ? new TranslationMachine(this, chosen, server) : null;

        public IReadOnlyList<string> Available(TranslationSetupV1 setup)
        {
            var installed = machines.Installed(setup.Machines);
            return [.. OllamaLocalModels.Candidates.Where(model => installed.Contains(model, StringComparer.Ordinal))];
        }

        public Task<string> DigestAsync(TranslationMachine machine, CancellationToken ct) => Task.FromResult(machines.Digest(machine.Address, machine.Model));

        public Task<string?> TranslateBlockAsync(TranslationMachine machine, string source, string target, string text, string? context, CancellationToken ct) =>
            models.TranslateBlockAsync(machine.Address, machine.Model, source, target, text, context, ct);

        public async Task<string> TranslateAsync(TranslationMachine machine, string source, string target, string text, CancellationToken ct) =>
            (await models.TranslateAsync(machine.Address, machine.Model, source, target, text, ct)).Text;

        public void Fail(TranslationMachine machine) => machines.Fail(machine.Address);

        public bool IsAway(TranslationSetupV1 setup) => machines.AllAway(setup.Machines);
    }
}
