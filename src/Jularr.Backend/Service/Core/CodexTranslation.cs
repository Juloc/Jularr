using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Integrations.Data.Codex;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.Translation;

namespace Jularr.Backend.Service.Core
{
    // The Owner's ChatGPT account as a translator through the provider seam, model ids "codex:<model>". It translates only when asked by name (the Owner's default
    // model or a re-translation), never as the automatic choice. The effort, fallback model and limits the Owner set ride on the machine's address (codex://local/?...):
    // the seam hands a machine to every call, and that is all a call knows.
    internal sealed class CodexTranslationProvider(CodexConnection connection, TimeProvider time) : ITranslationProvider
    {
        public const string ProviderKey = "codex";
        private static readonly TimeSpan Rest = TimeSpan.FromMinutes(1);
        private DateTimeOffset _restingUntil;

        public string Key => ProviderKey;

        // The models the account offered when last asked, never a guess; empty until the AI page or the first translation has asked.
        public IReadOnlyList<string> Models
        {
            get
            {
                connection.WarmModels();
                return connection.KnownModels;
            }
        }

        // The Owner's ChatGPT account: only administrators choose its models in the Reader.
        public bool IsAdminOnly => true;

        public async Task<IReadOnlyList<string>> AvailableAsync(TranslationSetupV1 setup, CancellationToken ct) =>
            connection.LimitReached(setup.CodexDailyTokens, setup.CodexRunTokens) || !await connection.IsConnectedAsync(ct) ? [] : Models;

        public async Task<TranslationMachine?> ChooseAsync(TranslationSetupV1 setup, string? model, CancellationToken ct)
        {
            if (model is null || time.GetUtcNow() < _restingUntil || connection.LimitReached(setup.CodexDailyTokens, setup.CodexRunTokens)) return null;
            if (!await connection.IsConnectedAsync(ct)) return null;
            var query = new List<string>();
            if (setup.CodexEffort is not null) query.Add("effort=" + Uri.EscapeDataString(setup.CodexEffort));
            if (setup.CodexFallbackModel is not null && setup.CodexFallbackModel != model) query.Add("fallback=" + Uri.EscapeDataString(setup.CodexFallbackModel));
            query.Add("daily=" + setup.CodexDailyTokens.ToString(CultureInfo.InvariantCulture));
            query.Add("run=" + setup.CodexRunTokens.ToString(CultureInfo.InvariantCulture));
            return new TranslationMachine(this, model, new Uri("codex://local/?" + string.Join('&', query)));
        }

        // The model alone: a changed effort or limit never makes the Owner's earlier translations stale.
        public Task<string> DigestAsync(TranslationMachine machine, CancellationToken ct) => Task.FromResult("codex-" + machine.Model);

        public async Task<string?> TranslateBlockAsync(TranslationMachine machine, string source, string target, string text, string? context, CancellationToken ct)
        {
            var answer = await AskAsync(machine, Prompt(source, target, text, context), ct);
            return OllamaLocalModels.LooksComplete(text, answer, target) ? answer : null;
        }

        public Task<string> TranslateAsync(TranslationMachine machine, string source, string target, string text, CancellationToken ct) =>
            AskAsync(machine, Prompt(source, target, text, null, "Keep every cue number and line break exactly as given; translate only the spoken text."), ct);

        public void Fail(TranslationMachine machine) => _restingUntil = time.GetUtcNow() + Rest;

        // The chosen model; when it fails the Owner's fallback model answers instead. Every answer's tokens count against the limits.
        private async Task<string> AskAsync(TranslationMachine machine, string prompt, CancellationToken ct)
        {
            var query = Query(machine.Address);
            if (connection.LimitReached(Number(query, "daily"), Number(query, "run"))) throw new InvalidDataException("The Codex token limit is reached.");
            query.TryGetValue("effort", out var effort);
            try
            {
                return await connection.ExecAsync(machine.Model, effort, prompt, ct);
            }
            catch (InvalidDataException) when (query.TryGetValue("fallback", out var fallback))
            {
                return await connection.ExecAsync(fallback, effort, prompt, ct);
            }
        }

        private static string Prompt(string source, string target, string text, string? context, string? extra = null)
        {
            var sourceName = CultureInfo.GetCultureInfo(source).EnglishName;
            var targetName = CultureInfo.GetCultureInfo(target).EnglishName;
            var prompt = $"You are a professional literary translator from {sourceName} ({source}) to {targetName} ({target}). Translate the text below. "
                + "Keep the narrator's point of view, the speakers and the character names; use the established names and terms of the work when you know it; do not invent, omit or explain anything. "
                + "Answer with the translation only, no notes, no quotation marks around it.\n";
            if (extra is not null) prompt += extra + "\n";
            if (!string.IsNullOrWhiteSpace(context))
                prompt += "The passage before it, for context only (do not translate or repeat it):\n" + (context.Length > 1200 ? context[^1200..] : context).Trim() + "\n";
            return prompt + "\nText to translate:\n\n" + text;
        }

        private static Dictionary<string, string> Query(Uri address) =>
            address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

        private static long Number(Dictionary<string, string> query, string name) =>
            query.TryGetValue(name, out var value) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
