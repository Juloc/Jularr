using Jularr.Web.Data;
using Jularr.Web.Features.Acquisition.Access;
using Jularr.Web.Features.Acquisition.Indexers;
using Jularr.Web.Features.Acquisition.Quality;
using Jularr.Web.Features.Acquisition.Release;
using Jularr.Web.Features.Acquisition.Selection;
using Jularr.Web.Features.Auth;
using Jularr.Web.Features.Instance;
using Jularr.Web.Features.Library;
using Jularr.Web.Features.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Jularr.Web.Pages.Admin;

public sealed record AcquisitionProfileRow(QualityProfile Profile, IReadOnlyList<MediaAcquisitionKind> DefaultKinds, int Titles);

public sealed record ProfileQualityRowView(string Quality, bool TakenAtOnce, bool IsFinal, UiTextBundle Ui);

public sealed record ProfileRuleRowView(ScoreRuleRow Row, string Index, IReadOnlyList<ReleaseRuleField> Fields, UiTextBundle Ui);

/// <summary>One indexer as the Sources section offers it: a configured entry, or an id the profile still names that no longer exists (kept until the owner unticks it, so deleting an indexer never widens the profile).</summary>
public sealed record ProfileSourceRow(Guid Id, string Name, bool Enabled, bool Missing);

/// <summary>What was typed into the profile test, kept so the result page shows it again.</summary>
public sealed record ProfileTestInput(string? Title, string? SizeMegabytes, Guid? Source);

/// <summary>
/// The one Admin editor of Acquisition Profiles, for every media type that has an acquisition kind: the profile list with where each is used,
/// and the selected profile's quality order, upgrade policy, release rules and sources, with a test of one release name against the profile as edited. It edits the generic <see cref="QualityProfile"/>
/// of the shared store; a media type only decides which profile is its default and which rule fields its releases carry.
/// </summary>
[Authorize(Policy = JularrPolicies.AcquisitionSettings)]
public sealed class AcquisitionProfilesModel(AppDbContext db, QualityProfileStore store, MediaAcquisitionRegistry registry, IInstanceModuleService modules, IndexerStore indexerStore, TimeProvider? clock = null) : PageModel
{
    public IReadOnlyList<ProfileSourceRow> Sources { get; private set; } = [];

    public ProfileTestResult? TestResult { get; private set; }

    public ProfileTestInput TestInput { get; private set; } = new(null, null, null);

    public UiTextBundle Ui { get; private set; } = UiTextBundle.English;

    public IReadOnlyList<AcquisitionProfileRow> Rows { get; private set; } = [];

    public IReadOnlyList<MediaAcquisitionKind> Kinds { get; private set; } = [];

    public AcquisitionProfileRow? Selected { get; private set; }

    public QualityProfileForm Form { get; private set; } = new();

    public IReadOnlyList<ProfileEditError> Errors { get; private set; } = [];

    public string? Notice => TempData["ProfilesNotice"] as string;

    public string? Warning => TempData["ProfilesError"] as string;

    public async Task<IActionResult> OnGetAsync(string? id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(id, cancellationToken);
        if (Selected is null && !string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        if (Selected is not null)
        {
            Form = QualityProfileEditing.ToForm(Selected.Profile);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(QualityProfileForm form, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(form.Id, cancellationToken);
        if (Selected is null)
        {
            return NotFound();
        }

        var result = QualityProfileEditing.Parse(form);
        var errors = result.Errors.ToList();
        if (result.Profile is { } profile)
        {
            var allowed = Selected.DefaultKinds.SelectMany(RuleFieldsOf).Distinct().ToHashSet();
            for (var index = 0; allowed.Count > 0 && index < profile.ScoreRules.Length; index++)
            {
                if (!allowed.Contains(profile.ScoreRules[index].Field))
                {
                    errors.Add(new ProfileEditError(nameof(QualityProfileForm.Rules), index + 1, "ruleField", profile.ScoreRules[index].Field.ToString()));
                }
            }

            if (errors.Count == 0)
            {
                await store.UpsertAsync(profile, cancellationToken);
                TempData["ProfilesNotice"] = Ui["admin.profiles.saved"];
                return RedirectToPage(new { id = profile.Id });
            }
        }

        Errors = errors;
        Form = form;
        return Page();
    }

    /// <summary>Runs one release name through the shared selection engine with the profile exactly as it is in the form, saving nothing.</summary>
    public async Task<IActionResult> OnPostTestAsync(QualityProfileForm form, string? testTitle, string? testSizeMegabytes, Guid? testSource, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(form.Id, cancellationToken);
        if (Selected is null)
        {
            return NotFound();
        }

        var result = QualityProfileEditing.Parse(form);
        Errors = result.Errors;
        Form = form;
        TestInput = new ProfileTestInput(testTitle, testSizeMegabytes, testSource);
        if (result.Profile is { } profile && !string.IsNullOrWhiteSpace(testTitle))
        {
            long? size = long.TryParse(testSizeMegabytes, out var megabytes) && megabytes > 0 ? megabytes * 1024 * 1024 : null;
            TestResult = ProfileTest.Run(profile, registry.ParserFor(KindsOf(Selected)[0]), testTitle.Trim(), size, testSource);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostNewAsync(MediaAcquisitionKind kind, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!registry.Supports(kind))
        {
            return BadRequest();
        }

        return RedirectToPage(new { id = await CreateFromAsync(registry.DefaultProfileFor(kind), Ui["admin.profiles.newName"], cancellationToken) });
    }

    public async Task<IActionResult> OnPostDuplicateAsync(string id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        var state = await store.LoadAsync(cancellationToken);
        if (state.Profiles.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { } source)
        {
            return NotFound();
        }

        return RedirectToPage(new { id = await CreateFromAsync(source, Ui.Format("admin.profiles.copyName", ("name", source.Name)), cancellationToken) });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string id, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        await LoadAsync(id, cancellationToken);
        if (Selected is null)
        {
            return NotFound();
        }

        if (await store.DeleteAsync(id, cancellationToken))
        {
            TempData["ProfilesNotice"] = Ui["admin.profiles.deleted"];
            return RedirectToPage();
        }

        TempData["ProfilesError"] = Ui.Format("admin.profiles.deleteBlocked", ("usage", UsageText(Selected)));
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSetDefaultAsync(string id, MediaAcquisitionKind kind, CancellationToken cancellationToken)
    {
        Ui = await UiRequestLocalization.GetBundleAsync(HttpContext, db);
        if (!registry.Supports(kind))
        {
            return BadRequest();
        }

        await store.SetKindDefaultAsync(kind, id, cancellationToken);
        TempData["ProfilesNotice"] = Ui["admin.profiles.saved"];
        return RedirectToPage(new { id });
    }

    /// <summary>The verdict of a profile test in words: taken now, left to a person, or not taken.</summary>
    public string TestVerdict(ProfileTestResult test) =>
        test.SourceProblem is not null ? Ui["admin.profiles.test.notFound"]
            : test.Decision switch
            {
                SelectionDecision.Eligible => Ui["admin.profiles.test.grab"],
                SelectionDecision.ManualReview => Ui["admin.profiles.test.manual"],
                _ => Ui["admin.profiles.test.rejected"]
            };

    public string KindLabel(MediaAcquisitionKind kind) => Ui[MediaKindLabelKeys.Name(kind)];

    public string UsageText(AcquisitionProfileRow row)
    {
        var parts = new List<string>();
        if (row.DefaultKinds.Count > 0)
        {
            parts.Add(Ui.Format("admin.profiles.defaultFor", ("kinds", string.Join(", ", row.DefaultKinds.Select(KindLabel)))));
        }

        if (row.Titles > 0)
        {
            parts.Add(Ui.Format("admin.profiles.usedBy", ("titles", row.Titles)));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The qualities the importer and parser of the media types of a profile can tell apart: what its order may rank.</summary>
    public IReadOnlyList<string> KnownQualities(AcquisitionProfileRow row) =>
        [.. KindsOf(row).SelectMany(kind => registry.DefaultProfileFor(kind).QualityOrder).Distinct(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<ReleaseRuleField> RuleFields(AcquisitionProfileRow row) =>
        [.. KindsOf(row).SelectMany(RuleFieldsOf).Distinct()];

    /// <summary>The media types a profile serves: those it is the default of, or else those whose default profile ranks any of its qualities (so a copy of a video profile is not offered audio formats), or all of them.</summary>
    private IReadOnlyList<MediaAcquisitionKind> KindsOf(AcquisitionProfileRow row)
    {
        if (row.DefaultKinds.Count > 0)
        {
            return row.DefaultKinds;
        }

        var related = Kinds.Where(kind => registry.DefaultProfileFor(kind).QualityOrder.Intersect(row.Profile.QualityOrder, StringComparer.OrdinalIgnoreCase).Any()).ToArray();
        return related.Length > 0 ? related : Kinds;
    }

    public string ErrorText(ProfileEditError error) =>
        error.Code switch
        {
            "number" => Ui.Format("admin.profiles.error.number", ("field", Ui[$"admin.profiles.field.{FieldKey(error.Field)}"])),
            "tier" => Ui.Format("admin.profiles.error.tier", ("line", error.Line ?? 0)),
            "rule" => Ui.Format("admin.profiles.error.rule", ("line", error.Line ?? 0)),
            "ruleField" => Ui.Format("admin.profiles.error.ruleField", ("line", error.Line ?? 0), ("field", error.Detail ?? "")),
            "quality" => Ui.Format("admin.profiles.error.quality", ("quality", error.Detail ?? "")),
            "allowed" => Ui["admin.profiles.error.allowed"],
            _ => Ui.Format("admin.profiles.error.profile", ("detail", error.Detail ?? ""))
        };

    private static string FieldKey(string field) => char.ToLowerInvariant(field[0]) + field[1..];

    /// <summary>The release facts a media type's parser actually produces, so a rule can only be written about something that is there.</summary>
    private static IEnumerable<ReleaseRuleField> RuleFieldsOf(MediaAcquisitionKind kind) =>
        kind is MediaAcquisitionKind.Movie or MediaAcquisitionKind.Tv or MediaAcquisitionKind.Anime
            ? Enum.GetValues<ReleaseRuleField>()
            : [ReleaseRuleField.RawTitle, ReleaseRuleField.ReleaseGroup, ReleaseRuleField.Indexer];

    private async Task LoadAsync(string? id, CancellationToken cancellationToken)
    {
        var settings = await modules.GetAsync(cancellationToken);
        Kinds = [.. registry.Kinds.Where(kind => settings.IsEnabled(AcquisitionInstanceModules.For(kind))).OrderBy(kind => kind.ToString(), StringComparer.Ordinal)];
        var state = await store.LoadAsync(cancellationToken);
        Rows =
        [
            .. state.Profiles
                .Select(profile => new AcquisitionProfileRow(
                    profile,
                    [.. registry.Kinds.Where(kind => state.DefaultProfileIdFor(kind)?.Equals(profile.Id, StringComparison.OrdinalIgnoreCase) == true)],
                    state.WorkAssignments.Values.Count(assigned => assigned.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(row => row.Profile.Name, StringComparer.OrdinalIgnoreCase)
        ];
        Selected = Rows.FirstOrDefault(row => row.Profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? (string.IsNullOrWhiteSpace(id) ? Rows.FirstOrDefault() : null);

        var entries = await indexerStore.LoadAllAsync(cancellationToken);
        var named = Selected?.Profile.SourcePolicy.AllowedEntryIds.Concat(Selected.Profile.SourcePolicy.PreferredEntryIds).Where(entry => entries.All(known => known.Id != entry)).Distinct() ?? [];
        Sources = [.. entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).Select(entry => new ProfileSourceRow(entry.Id, entry.Name, entry.Enabled, false)), .. named.Select(entry => new ProfileSourceRow(entry, entry.ToString("D")[..8], false, true))];
    }

    private async Task<string> CreateFromAsync(QualityProfile source, string name, CancellationToken cancellationToken)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-')]).Trim('-');
        var id = $"{(slug.Length == 0 ? "profile" : slug[..Math.Min(32, slug.Length)])}-{Guid.NewGuid().ToString("N")[..8]}";
        await store.UpsertAsync(source with { Id = id, Name = name }, cancellationToken);
        return id;
    }
}
