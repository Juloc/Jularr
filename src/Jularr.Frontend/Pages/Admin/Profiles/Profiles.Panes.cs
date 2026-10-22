using System.Globalization;
using Jularr.Backend.Data.Admin.Acquisition.V1;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Acquisition;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Frontend.Framework;
using Microsoft.AspNetCore.Mvc;

namespace Jularr.Frontend.Pages.Admin.Profiles
{
    public sealed partial class ProfilesModel
    {
        private void DefineFormats(Page page)
        {
            if (_formats is not { } result) return;

            // Which profiles give a format a score decides whether it is used.
            var empty = new Div(new IconTile(Glyph("filter", 22), UiTone.Neutral), new P(new Text("arr.formats.none")).Attribute("role", "status"));
            var columns = new List<ListColumn>
            {
                new("profile.name", ListRole.Main), new("table.status", ListRole.Status), new("arr.col.conditions", ListRole.Data, 1), new("arr.col.renaming", ListRole.Data, 2),
                new("arr.col.profiles", ListRole.Data, 2), new("common.actions", ListRole.Actions)
            };
            var rows = result.Items.Select(item =>
            {
                var using_ = _profiles.Count(profile => profile.FormatScores.Any(score => score.FormatId == item.Id && score.Score != 0));
                var identity = new ListIdentity(new IconTile(Glyph("filter", 18), UiTone.Info), new A(new ValueText(item.Name)).Class("list-identity__title").Attribute("href", Address(edit: item.Id)),
                    new Truncated(string.Join(", ", item.Conditions.Select(condition => condition.Name.Length > 0 ? condition.Name : texts["arr.condition." + condition.Kind]))));
                var state = using_ > 0 ? new StateChip(new Text("arr.format.used"), UiTone.Success, Glyph("check", 14)) : new StateChip(new Text("arr.format.unused"), UiTone.Neutral, Glyph("clock", 14));
                var renaming = item.IncludeWhenRenaming ? new Span(new Text("arr.col.renaming.yes")).Class("list-value") : new Span(new ValueText("–")).Class("list-value list-value--dim");
                var caption = new List<IUiComponent> { ListPanel.Fact("arr.col.conditions", Count(item.Conditions.Count)), ListPanel.Fact("arr.col.profiles", Count(using_)) };
                return new ListRow([identity, state, new Span(Count(item.Conditions.Count)).Class("list-value"), renaming, new Span(Count(using_)).Class("list-value"), new Div(EditLink(Address(edit: item.Id))).Class("list-actions")], caption);
            }).ToList();
            page.Content.Add(new ListPanel("arr.formats", columns, rows, empty).Modifier("formats-list").Paged());

            // The editor and the import, each a dialog.
            var input = _format ?? new SaveCustomFormatDataV1(null, MediaTypeId, "", false,
                new([new FormatConditionV1("", FormatConditionKind.ReleaseTitle, false, false, null, false, null, null, null)]), null);
            var form = new Form<SaveCustomFormatDataV1>(form =>
            {
                form.Hidden(item => item.Id);
                form.Hidden(item => item.MediaTypeId);
                form.Hidden(item => item.Version);
                form.Text(item => item.Name, "profile.name");
                form.Toggle(item => item.IncludeWhenRenaming, "arr.includeRenaming");
                form.Rows(item => item.Conditions, (row, condition) =>
                {
                    row.Text(item => item.Name, "profile.name");
                    row.Select(item => item.Kind, "arr.condition", FormatConditionKinds.For(MediaTypeId)
                        .Select(kind => new SelectOption(Number((byte)kind), texts["arr.condition." + kind])),
                        refreshAction: Address("Condition", Edit) + "&refresh=true", refreshLabelKey: "arr.applyCondition");
                    row.Toggle(item => item.Required, "arr.required");
                    row.Toggle(item => item.Negate, "arr.negate");
                    switch (condition.Kind)
                    {
                        case FormatConditionKind.Resolution:
                            row.Number(item => item.Resolution, "arr.resolution");
                            break;
                        case FormatConditionKind.Size or FormatConditionKind.Year:
                            row.Number(item => item.Minimum, "arr.minimum");
                            row.Number(item => item.Maximum, "arr.maximum");
                            break;
                        default:
                            row.Text(item => item.Value, "arr.value", help: new FieldHelp(Info: "arr.condition.info"));
                            if (condition.Kind == FormatConditionKind.Language) row.Toggle(item => item.ExceptLanguage, "arr.exceptLanguage");
                            break;
                    }
                });
                form.Content(new Button(new Text("arr.addCondition")).Attribute("type", "submit").Attribute("formaction", Address("Condition", Edit))
                    .Attribute("name", "add").Attribute("value", "true").Attribute("formnovalidate", "formnovalidate"));
                if (input.Conditions.Count > 1)
                {
                    form.Content(new Button(new Text("arr.removeCondition")).Attribute("type", "submit").Attribute("formaction", Address("Condition", Edit))
                        .Attribute("formnovalidate", "formnovalidate"));
                }
                form.Cancel();
                form.Submit("common.save");
            }, Address("Format", Edit), Token, input, _import is null ? FormError : null);
            var body = new List<IUiComponent> { form };
            if (input.Id is { } id) body.Add(DeleteForm(id, input.Version!.Value));
            if (Edit is not null || IsNew || _format is not null) page.Content.Add(new Modal(assets, "format-editor", "arr.format.title", body, open: true, returnTo: Address()));

            var import = new Form<ImportCustomFormatsDataV1>(form =>
            {
                form.Hidden(item => item.MediaTypeId);
                form.Select(item => item.TakeoverSourceTypeId, "takeover.source",
                    new[] { TakeoverSourceType.Sonarr, TakeoverSourceType.Radarr }.Select(source => new SelectOption(Number((byte)source), texts["takeover.source." + source])));
                form.Multiline(item => item.Json, "arr.importJson");
                form.Cancel();
                form.Submit("arr.import");
            }, Address("Import"), Token, _import ?? new ImportCustomFormatsDataV1(MediaTypeId, TakeoverSourceType.Radarr, ""), _import is null ? null : FormError);
            page.Content.Add(new Modal(assets, "format-import", "arr.import", [import], open: _import is not null));

            var actions = new List<IUiComponent>
            {
                AddAction("arr.format.add"),
                Modal.Opener(new Button(Glyph("download", 16), new Span(new Text("arr.import")).Class("action-dock__text")).Soft(), "format-import")
            };
            page.Content.Add(new ActionDock(assets, "arr.actions", actions));
        }

        private void DefineSizes(Page page)
        {
            if (_sizes is null || _sizes.Rows.Count == 0) return;
            page.Content.Add(new Form<SaveQualityDefinitionsDataV1>(form =>
            {
                form.Rows(item => item.Rows, (row, item) =>
                {
                    row.Hidden(value => value.Quality);
                    row.Hidden(value => value.Version);
                    row.Content(new P(new ValueText(item.Quality)));
                    row.Number(value => value.MinimumMbPerMinute, "arr.minimumMb");
                    row.Number(value => value.PreferredMbPerMinute, "arr.preferredMb");
                    row.Number(value => value.MaximumMbPerMinute, "arr.maximumMb");
                });
                form.Save();
            }, Address("Sizes"), Token, _sizes, FormError));
        }

        private void DefineRelease(Page page)
        {
            if (_releases is not { } result) return;
            var empty = new Div(new IconTile(Glyph("shield", 22), UiTone.Neutral), new P(new Text("arr.release.none")).Attribute("role", "status"));
            var columns = new List<ListColumn>
            {
                new("profile.name", ListRole.Main), new("table.status", ListRole.Status), new("arr.col.required", ListRole.Data, 1), new("arr.col.ignored", ListRole.Data, 1),
                new("arr.col.indexer", ListRole.Data, 2), new("common.actions", ListRole.Actions)
            };
            var rows = result.Items.Select(item =>
            {
                var identity = new ListIdentity(new IconTile(Glyph("shield", 18), UiTone.Warning), new A(new ValueText(item.Name)).Class("list-identity__title").Attribute("href", Address(edit: item.Id)),
                    new Truncated(string.Join(", ", item.Required.Concat(item.Ignored))));
                var state = item.IsEnabled ? new StateChip(new Text("acquisition.connection.on"), UiTone.Success, Glyph("check", 14)) : new StateChip(new Text("acquisition.connection.off"), UiTone.Neutral, Glyph("power", 14));
                var indexer = item.AcquisitionConnectionId is { } connectionId ? _indexerChoices.FirstOrDefault(choice => choice.Value == Number(connectionId))?.Label ?? "–" : texts["arr.anyIndexer"];
                var caption = new List<IUiComponent> { ListPanel.Fact("arr.col.required", Count(item.Required.Length)), ListPanel.Fact("arr.col.ignored", Count(item.Ignored.Length)) };
                return new ListRow([identity, state, new Span(Count(item.Required.Length)).Class("list-value"), new Span(Count(item.Ignored.Length)).Class("list-value"),
                    new Span(new ValueText(indexer)).Class("list-value"), new Div(EditLink(Address(edit: item.Id))).Class("list-actions")], caption);
            }).ToList();
            page.Content.Add(new ListPanel("arr.releaseProfiles", columns, rows, empty).Modifier("release-list").Paged());

            var input = _release ?? new SaveReleaseProfileDataV1(null, MediaTypeId, "", true, [], [], null, [], null);
            var form = new Form<SaveReleaseProfileDataV1>(form =>
            {
                form.Hidden(item => item.Id);
                form.Hidden(item => item.Version);
                form.Text(item => item.Name, "profile.name");
                form.Toggle(item => item.IsEnabled, "admin.providers.enabled");
                form.Multiline(item => item.Required, "arr.requiredTerms", help: new FieldHelp(Info: "arr.terms.info"));
                form.Multiline(item => item.Ignored, "arr.ignoredTerms", help: new FieldHelp(Info: "arr.terms.info"));
                form.Select(item => item.AcquisitionConnectionId, "acquisition.role.Search", _indexerChoices);
                PickerPaging(form, "Release", "IndexerPage", _indexerPage?.Page ?? 1, _indexerPage?.Items.Count == _indexerPage?.PageSize);
                form.Choices(item => item.TagIds, "arr.tags", _tagChoices);
                PickerPaging(form, "Release", "TagPage", _tagPage?.Page ?? 1, _tagPage?.Items.Count == _tagPage?.PageSize);
                form.Cancel();
                form.Submit("common.save");
            }, Address("Release", Edit), Token, input, FormError);
            var body = new List<IUiComponent> { form };
            if (input.Id is { } id) body.Add(DeleteForm(id, input.Version!.Value));
            if (Edit is not null || IsNew || _release is not null) page.Content.Add(new Modal(assets, "release-editor", "arr.release.title", body, open: true, returnTo: Address()));
            page.Content.Add(new ActionDock(assets, "arr.actions", [AddAction("arr.release.add")]));
        }

        private void DefineTags(Page page)
        {
            if (_tags is not { } result) return;
            var empty = new Div(new IconTile(Glyph("tag", 22), UiTone.Neutral), new P(new Text("arr.tags.none")).Attribute("role", "status"));
            var columns = new List<ListColumn> { new("arr.tag", ListRole.Main), new("common.actions", ListRole.Actions) };
            var rows = result.Items.Select(item => new ListRow([
                new ListIdentity(new IconTile(Glyph("tag", 18), UiTone.Accent), new A(new ValueText(item.Label)).Class("list-identity__title").Attribute("href", Address(edit: item.Id)), new Span()),
                new Div(EditLink(Address(edit: item.Id))).Class("list-actions")])).ToList();
            page.Content.Add(new ListPanel("arr.tags", columns, rows, empty).Modifier("tags-list").Paged());

            var input = _tag ?? new SaveAcquisitionTagDataV1(null, "", null);
            var form = new Form<SaveAcquisitionTagDataV1>(form =>
            {
                form.Hidden(item => item.Id);
                form.Hidden(item => item.Version);
                form.Text(item => item.Label, "arr.tag");
                form.Cancel();
                form.Submit("common.save");
            }, Address("Tag", Edit), Token, input, FormError);
            var body = new List<IUiComponent> { form };
            if (input.Id is { } id) body.Add(DeleteForm(id, input.Version!.Value));
            if (Edit is not null || IsNew || _tag is not null) page.Content.Add(new Modal(assets, "tag-editor", "arr.tag.title", body, open: true, returnTo: Address()));
            page.Content.Add(new ActionDock(assets, "arr.actions", [AddAction("arr.tag.add")]));
        }

        // The deletion of the edited item behind one more click.
        private IUiComponent DeleteForm(long id, long version)
        {
            IUiComponent form = Tab switch
            {
                DownloadPages.Formats => Confirm(new DeleteCustomFormatParametersV1(id, version), fields =>
                {
                    fields.Hidden(item => item.CustomFormatId);
                    fields.Hidden(item => item.Version);
                }),
                DownloadPages.Release => Confirm(new DeleteReleaseProfileParametersV1(id, version), fields =>
                {
                    fields.Hidden(item => item.ReleaseProfileId);
                    fields.Hidden(item => item.Version);
                }),
                DownloadPages.Tags => Confirm(new DeleteAcquisitionTagParametersV1(id, version), fields =>
                {
                    fields.Hidden(item => item.AcquisitionTagId);
                    fields.Hidden(item => item.Version);
                }),
                _ => throw new KeyNotFoundException()
            };
            return new Details(new Summary(new Text("common.remove")), form);

            Form<T> Confirm<T>(T input, Action<Form<T>> fields) where T : BaseData => new(form =>
            {
                fields(form);
                form.Submit("common.remove");
            }, Address("Delete", id), Token, input, FormError);
        }

        public async Task<IActionResult> OnPostFormatAsync(long? id, string? name, bool includeWhenRenaming, long? version, List<ConditionRow>? conditions)
        {
            Tab = DownloadPages.Formats;
            _format = FormatInput(id, name, includeWhenRenaming, version, conditions);
            await saveFormat.ExecuteAsync<SavedCustomFormatV1>(new SaveCustomFormatParametersV1(), _format, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostConditionAsync(long? id, string? name, bool includeWhenRenaming, long? version, List<ConditionRow>? conditions, bool add, bool refresh = false)
        {
            Tab = DownloadPages.Formats;
            var rows = conditions ?? [];
            if (add && rows.Count < 64) rows.Add(new("", FormatConditionKind.ReleaseTitle, false, false, null, false, null, null, null));
            else if (!add && !refresh && rows.Count > 1) rows.RemoveAt(rows.Count - 1);
            _format = FormatInput(id, name, includeWhenRenaming, version, rows);
            await LoadDataAsync();
            PreparePage();
            return Page();
        }

        private SaveCustomFormatDataV1 FormatInput(long? id, string? name, bool renaming, long? version, List<ConditionRow>? rows) =>
            new(id, MediaTypeId, name ?? "", renaming, new((rows ?? []).Select(row => new FormatConditionV1(row.Name ?? "", row.Kind, row.Negate,
                row.Required, row.Value, row.ExceptLanguage, row.Resolution, row.Minimum, row.Maximum))), version);

        public async Task<IActionResult> OnPostImportAsync(TakeoverSourceType takeoverSourceTypeId, string? json)
        {
            Tab = DownloadPages.Formats;
            _import = new(MediaTypeId, takeoverSourceTypeId, json ?? "");
            await importFormats.ExecuteAsync<BaseDataList<SavedCustomFormatV1>>(new SaveCustomFormatParametersV1(), _import, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostSizesAsync(List<SizeRow>? rows)
        {
            Tab = DownloadPages.Sizes;
            _sizes = new(new((rows ?? []).Select(row => new QualityDefinitionV1(row.Quality ?? "", row.MinimumMbPerMinute,
                row.PreferredMbPerMinute, row.MaximumMbPerMinute, row.Version))));
            await saveSizes.ExecuteAsync<UpdateResult>(new QualityDefinitionsParametersV1(MediaTypeId), _sizes, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostReleaseAsync(long? id, string? name, bool isEnabled, string? required, string? ignored,
            long? acquisitionConnectionId, long[]? tagIds, long? version, bool draft)
        {
            Tab = DownloadPages.Release;
            var scope = id is { } existing ? (await release.ExecuteAsync<ReleaseProfileDetailsV1>(new ReleaseProfileParametersV1(existing), HttpContext.RequestAborted)).MediaTypeId : MediaTypeId;
            _release = new(id, scope, name ?? "", isEnabled, Terms(required), Terms(ignored), acquisitionConnectionId, tagIds ?? [], version);
            if (draft) { await LoadDataAsync(); PreparePage(); return Page(); }
            await saveRelease.ExecuteAsync<SavedAcquisitionPolicyV1>(new SaveAcquisitionPolicyParametersV1(), _release, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostTagAsync(long? id, string? label, long? version)
        {
            Tab = DownloadPages.Tags;
            _tag = new(id, label ?? "", version);
            await saveTag.ExecuteAsync<SavedAcquisitionPolicyV1>(new SaveAcquisitionPolicyParametersV1(), _tag, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostDeleteAsync(long version)
        {
            var id = Edit ?? throw new KeyNotFoundException();
            switch (Tab)
            {
                case DownloadPages.Formats: await deleteFormat.ExecuteAsync<UpdateResult>(new DeleteCustomFormatParametersV1(id, version), default, HttpContext.RequestAborted); break;
                case DownloadPages.Release: await deleteRelease.ExecuteAsync<UpdateResult>(new DeleteReleaseProfileParametersV1(id, version), default, HttpContext.RequestAborted); break;
                case DownloadPages.Tags: await deleteTag.ExecuteAsync<UpdateResult>(new DeleteAcquisitionTagParametersV1(id, version), default, HttpContext.RequestAborted); break;
                default: throw new KeyNotFoundException();
            }
            Edit = null;
            return LocalRedirect(Address());
        }

        private void PickerPaging<T>(Form<T> form, string handler, string parameter, int current, bool more) where T : BaseData
        {
            if (current > 1) form.Content(PickerButton("common.previous", handler, parameter, current - 1));
            if (more) form.Content(PickerButton("common.next", handler, parameter, current + 1));
        }

        private IUiComponent PickerButton(string key, string handler, string parameter, int target) =>
            new Button(new Text(key)).Attribute("type", "submit").Attribute("formaction", Address(handler, Edit) + "&draft=true&" + parameter + "=" + Number(target))
                .Attribute("formnovalidate", "formnovalidate");

        private static string[] Terms(string? value) => value?.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        public sealed record ConditionRow(string? Name, FormatConditionKind Kind, bool Negate, bool Required, string? Value, bool ExceptLanguage, int? Resolution, decimal? Minimum, decimal? Maximum);
        public sealed record SizeRow(string? Quality, decimal MinimumMbPerMinute, decimal? PreferredMbPerMinute, decimal? MaximumMbPerMinute, long Version);
    }
}
