using System.Globalization;
using System.Text.Json;
using Jularr.Backend.Data.Admin.Translation.V1;
using Jularr.Integrations.Data.Codex;
using Jularr.Integrations.Data.Common;
using Jularr.Integrations.Logic.Translation;

namespace Jularr.Backend.Service.Core
{
    // A provider that can translate several blocks of a section in one request, which gives the model the whole passage as context. Answers are in the order of the
    // blocks; null where a block's answer is missing or fails the provider's checks (the caller asks for that block alone).
    internal interface IBatchTranslationProvider : ITranslationProvider
    {
        // The most source characters and the most blocks one request should carry: a chapter goes in several requests, each answered within a minute or so,
        // so a reader sees text arrive and a failed request loses little.
        int BatchCharacters { get; }

        int BatchBlocks { get; }

        // The model this provider offers for a button press now (the Owner's choice first) and what keeps it from translating: null for a model or a problem
        // that is not known; a problem is one of "not_connected", "limit", "unavailable".
        Task<(string? Model, string? Problem)> OfferAsync(TranslationSetupV1 setup, CancellationToken ct);

        Task<string?[]> TranslateBlocksAsync(TranslationMachine machine, string source, string target, IReadOnlyList<string> blocks, string? context, CancellationToken ct);

        // One chapter's requests counted together until disposed (chapter from 1), for comparing what a chapter costs with each model.
        IDisposable BeginChapter(TranslationMachine machine, int chapter, string? title);
    }

    // The Owner's ChatGPT account as a translator through the provider seam, model ids "codex:<model>". It translates only when asked by name (the Owner's default
    // model or a re-translation), never as the automatic choice. The effort, fallback model and limits the Owner set ride on the machine's address (codex://local/?...):
    // the seam hands a machine to every call, and that is all a call knows.
    internal sealed class CodexTranslationProvider(CodexConnection connection, TimeProvider time) : IBatchTranslationProvider
    {
        public const string ProviderKey = "codex";
        private static readonly TimeSpan Rest = TimeSpan.FromMinutes(1);
        private DateTimeOffset _restingUntil;

        public string Key => ProviderKey;

        // Short requests: a chapter in many of them, each with the passage around its blocks.
        public int BatchCharacters => 6_000;

        public int BatchBlocks => 30;

        // The models the account offered when last asked, never a guess; empty until the AI page or the first translation has asked.
        public IReadOnlyList<string> Models
        {
            get
            {
                connection.WarmModels();
                return connection.KnownModels;
            }
        }

        // The Owner's ChatGPT account: only the admin-only right chooses its model in the Reader.
        public bool IsAdminOnly => true;

        // The account's models while Codex is connected and within its limits, the one chosen on the AI page first (the Owner compares them on one chapter).
        public async Task<IReadOnlyList<string>> AvailableAsync(TranslationSetupV1 setup, CancellationToken ct)
        {
            if (connection.PlanLimitedUntil is not null || connection.LimitReached(setup.CodexDailyTokens, setup.CodexRunTokens) || !await connection.IsConnectedAsync(ct)) return [];
            if (connection.KnownModels.Length == 0) await connection.ReadCatalogAsync(ct);
            var known = connection.KnownModels;
            return [.. known.Where(model => model == setup.CodexModel).Concat(known.Where(model => model != setup.CodexModel))];
        }

        public async Task<(string? Model, string? Problem)> OfferAsync(TranslationSetupV1 setup, CancellationToken ct)
        {
            if (!await connection.IsConnectedAsync(ct)) return (null, "not_connected");
            if (connection.KnownModels.Length == 0) await connection.ReadCatalogAsync(ct);
            var known = connection.KnownModels;
            var model = known.Contains(setup.CodexModel, StringComparer.Ordinal) ? setup.CodexModel : known.FirstOrDefault();
            if (connection.PlanLimitedUntil is not null || connection.LimitReached(setup.CodexDailyTokens, setup.CodexRunTokens)) return (model, "limit");
            return model is null ? (null, "unavailable") : (model, null);
        }

        public async Task<TranslationMachine?> ChooseAsync(TranslationSetupV1 setup, string? model, CancellationToken ct)
        {
            if (model is null || time.GetUtcNow() < _restingUntil || connection.PlanLimitedUntil is not null || connection.LimitReached(setup.CodexDailyTokens, setup.CodexRunTokens)) return null;
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
            var answer = await AskAsync(machine, Prompt(source, target, text, context), "translate a block", ct);
            return OllamaLocalModels.LooksComplete(text, answer, target) ? answer : null;
        }

        // The blocks numbered in one request: the model answers with the same numbers, and every block's answer is checked on its own.
        public async Task<string?[]> TranslateBlocksAsync(TranslationMachine machine, string source, string target, IReadOnlyList<string> blocks, string? context, CancellationToken ct)
        {
            if (blocks.Count == 1) return [await TranslateBlockAsync(machine, source, target, blocks[0], context, ct)];
            var numbered = string.Join("\n\n", blocks.Select((block, index) => "[[" + (index + 1).ToString(CultureInfo.InvariantCulture) + "]]\n" + block));
            var extra = "The text has " + blocks.Count.ToString(CultureInfo.InvariantCulture) + " numbered parts, each starting with a line [[n]]. Answer with the same lines [[n]] and the translation of each part under its own line; "
                + "never merge, split, skip or reorder parts.";
            var answer = await AskAsync(machine, Prompt(source, target, numbered, context, extra), "translate " + blocks.Count.ToString(CultureInfo.InvariantCulture) + " blocks", ct);
            var parts = Numbered(answer, blocks.Count);
            return [.. blocks.Select((block, index) => parts[index] is { } part && OllamaLocalModels.LooksComplete(block, part, target) ? part : null)];
        }

        // The parts of an answer by their [[n]] lines; null where a part is missing.
        internal static string?[] Numbered(string answer, int count)
        {
            var parts = new string?[count];
            var lines = answer.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var number = -1;
            var text = new List<string>();
            void Flush()
            {
                if (number is >= 1 && number <= count && parts[number - 1] is null)
                {
                    var joined = string.Join("\n", text).Trim();
                    if (joined.Length > 0) parts[number - 1] = joined;
                }

                text.Clear();
            }

            foreach (var line in lines)
            {
                var marker = line.Trim();
                if (marker.Length > 4 && marker.StartsWith("[[", StringComparison.Ordinal) && marker.EndsWith("]]", StringComparison.Ordinal)
                    && int.TryParse(marker.AsSpan(2, marker.Length - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var next))
                {
                    Flush();
                    number = next;
                }
                else text.Add(line);
            }

            Flush();
            return parts;
        }

        public IDisposable BeginChapter(TranslationMachine machine, int chapter, string? title) =>
            connection.BeginRun(chapter, title, machine.Model, Query(machine.Address).GetValueOrDefault("effort"));

        public Task<string> TranslateAsync(TranslationMachine machine, string source, string target, string text, CancellationToken ct) =>
            AskAsync(machine, Prompt(source, target, text, null, "Keep every cue number and line break exactly as given; translate only the spoken text."), "translate subtitles", ct);

        public void Fail(TranslationMachine machine) => _restingUntil = time.GetUtcNow() + Rest;

        // The chosen model; when it fails the Owner's fallback model answers instead. Every answer's tokens count against the limits.
        private async Task<string> AskAsync(TranslationMachine machine, string prompt, string what, CancellationToken ct)
        {
            var query = Query(machine.Address);
            if (connection.LimitReached(Number(query, "daily"), Number(query, "run"))) throw new InvalidDataException("The Codex token limit is reached.");
            query.TryGetValue("effort", out var effort);
            try
            {
                return await connection.ExecAsync(machine.Model, effort, prompt, what, ct);
            }
            catch (InvalidDataException) when (query.TryGetValue("fallback", out var fallback))
            {
                return await connection.ExecAsync(fallback, effort, prompt, what, ct);
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
