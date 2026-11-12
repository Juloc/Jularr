using System.Globalization;
using Jularr.Backend.Data;
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
        // The media types with quality sizes: films count megabytes per minute (also anime films), series too (also anime series), books per volume.
        private static readonly MediaType[] QualityTypes = [MediaType.Movie, MediaType.Series, MediaType.Book];

        // The release profiles' media types: none is every type.
        private static readonly MediaType[] ReleaseTypes = [MediaType.Movie, MediaType.Series, MediaType.Book, MediaType.Music, MediaType.Game];

        private async Task LoadFormatsAsync(CancellationToken cancellation)
        {
            // The formats; every profile names how it scores them.
            _formats = await formats.ExecuteAsync<PageResult<CustomFormatDetailsV1>>(new ReadCustomFormatsParametersV1(PageOf(PagedList.DefaultKey)), cancellation);
            _profiles = await profiles.ExecuteAsync<BaseDataList<AcquisitionProfileDetailsV1>>(new ReadAcquisitionProfilesParametersV1(), cancellation);
            if (_format is null && Item == FormatItem && Edit is { } formatId)
            {
                var item = await format.ExecuteAsync<CustomFormatDetailsV1>(new CustomFormatParametersV1(formatId), cancellation);
                _format = new(item.Id, item.Name, item.IncludeWhenRenaming, item.Conditions, item.Version);
            }
        }

        // The release profiles and the tags they apply to, each with its own page.
        private async Task LoadReleasesAsync(CancellationToken cancellation)
        {
            _releases = await releases.ExecuteAsync<PageResult<ReleaseProfileDetailsV1>>(new ReadAcquisitionPolicyParametersV1(null, PageOf("releases")), cancellation);
            _tags = await tags.ExecuteAsync<PageResult<AcquisitionTagDetailsV1>>(new ReadAcquisitionTagsParametersV1(PageOf("tags")), cancellation);

            // Every release card's chosen tags and indexer are offered besides the page of tags and indexers its pickers show.
            _tagPage = await tags.ExecuteAsync<PageResult<AcquisitionTagDetailsV1>>(new ReadAcquisitionTagsParametersV1(PageOf("tagChoices")), cancellation);
            var choices = _tagPage.Items.ToList();
            foreach (var id in (_release?.TagIds ?? []).Concat(_releases.Items.SelectMany(item => item.TagIds)).Distinct().Where(id => !choices.Any(choice => choice.Id == id)).Take(100).ToList())
                choices.Add(await tag.ExecuteAsync<AcquisitionTagDetailsV1>(new AcquisitionTagParametersV1(id), cancellation));
            _tagChoices = choices.Select(item => new SelectOption(Number(item.Id), item.Label)).ToArray();
            _indexerPage = await connections.ExecuteAsync<PageResult<AcquisitionConnectionDetailsV1>>(
                new ReadAcquisitionConnectionsParametersV1(PageOf("indexerChoices"), acquisitionConnectionRoleId: AcquisitionConnectionRole.Search), cancellation);
            var indexers = _indexerPage.Items.ToList();
            foreach (var id in new[] { _release?.AcquisitionConnectionId }.Concat(_releases.Items.Select(item => item.AcquisitionConnectionId)).OfType<long>().Distinct()
                .Where(id => !indexers.Any(item => item.Id == id)).Take(100))
                indexers.Add(await connection.ExecuteAsync<AcquisitionConnectionDetailsV1>(new AcquisitionConnectionParametersV1(id), cancellation));
            _indexerChoices = new[] { new SelectOption("", texts["arr.anyIndexer"]) }.Concat(indexers.Select(item => new SelectOption(Number(item.Id), item.Name))).ToArray();
        }

        // The custom formats once for every media type.
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
                var editor = Address(edit: item.Id, item: FormatItem);
                var identity = new ListIdentity(new IconTile(Glyph("filter", 18), UiTone.Info), new A(new ValueText(item.Name)).Class("list-identity__title").Attribute("href", editor),
                    new Truncated(string.Join(", ", item.Conditions.Select(condition => condition.Name.Length > 0 ? condition.Name : texts["arr.condition." + condition.Kind]))));
                var state = using_ > 0 ? new StateChip(new Text("arr.format.used"), UiTone.Success, Glyph("check", 14)) : new StateChip(new Text("arr.format.unused"), UiTone.Neutral, Glyph("clock", 14));
                var renaming = item.IncludeWhenRenaming ? new Span(new Text("arr.col.renaming.yes")).Class("list-value") : new Span(new ValueText("–")).Class("list-value list-value--dim");
                var caption = new List<IUiComponent> { ListPanel.Fact("arr.col.conditions", Count(item.Conditions.Count)), ListPanel.Fact("arr.col.profiles", Count(using_)) };
                return new ListRow([identity, state, new Span(Count(item.Conditions.Count)).Class("list-value"), renaming, new Span(Count(using_)).Class("list-value"), new Div(EditLink(editor)).Class("list-actions")], caption);
            }).ToList();
            page.Content.Add(new ListPanel("arr.formats", columns, rows, empty).Modifier("formats-list").Paged(primary: true));
            DefineFormatDialogs(page);

            var actions = new List<IUiComponent>
            {
                AddAction("arr.format.add", FormatItem),
                Modal.Opener(new Button(Glyph("download", 16), new Span(new Text("arr.import")).Class("action-dock__text")).Soft(), "format-import")
            };
            page.Content.Add(new ActionDock(assets, "arr.actions", actions));
        }

        // The release profiles, then the tags a title carries and a release profile asks for.
        private void DefineReleases(Page page)
        {
            page.Content.Add(ReleaseSection());
            page.Content.Add(TagSection());
            if (ConfirmDelete && Edit is { } removed && Item is ReleaseItem or TagItem)
            {
                var version = Item == ReleaseItem ? _releases?.Items.FirstOrDefault(item => item.Id == removed)?.Version : _tags?.Items.FirstOrDefault(item => item.Id == removed)?.Version;
                if (version is { } current) page.Content.Add(DeleteDialog(removed, current, Item));
            }

            page.Content.Add(new ActionDock(assets, "arr.actions", [AddAction("arr.release.add", ReleaseItem), AddAction("arr.tag.add", TagItem)]));
        }

        // A section of the tab under its own heading, reached by the address's anchor.
        private static IUiComponent PaneSection(string id, string headingKey, params IUiComponent[] content) =>
            new Section([new H2(new Text(headingKey)).Class("profiles-section__title"), .. content]).Class("profiles-section").Attribute("id", id);

        private void DefineFormatDialogs(Page page)
        {
            // The editor and the import, each a dialog.
            var input = _format ?? new SaveCustomFormatDataV1(null, "", false, new([new FormatConditionV1("", FormatConditionKind.ReleaseTitle, false, false, null, false, null, null, null)]), null);
            var conditionKinds = Enum.GetValues<FormatConditionKind>().Where(kind => ReleaseTypes.Any(type => FormatConditionKinds.Supports(type, kind)));
            var form = new Form<SaveCustomFormatDataV1>(form =>
            {
                form.Hidden(item => item.Id);
                form.Hidden(item => item.Version);
                form.Text(item => item.Name, "profile.name");
                form.Toggle(item => item.IncludeWhenRenaming, "arr.includeRenaming");
                form.Rows(item => item.Conditions, (row, condition) =>
                {
                    row.Text(item => item.Name, "profile.name");
                    row.Select(item => item.Kind, "arr.condition", conditionKinds.Select(kind => new SelectOption(Number((byte)kind), texts["arr.condition." + kind])),
                        refreshAction: Address("Condition", input.Id, FormatItem) + "&refresh=true", refreshLabelKey: "arr.applyCondition");
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
                form.Content(new Button(new Text("arr.addCondition")).Attribute("type", "submit").Attribute("formaction", Address("Condition", input.Id, FormatItem))
                    .Attribute("name", "add").Attribute("value", "true").Attribute("formnovalidate", "formnovalidate"));
                if (input.Conditions.Count > 1)
                {
                    form.Content(new Button(new Text("arr.removeCondition")).Attribute("type", "submit").Attribute("formaction", Address("Condition", input.Id, FormatItem))
                        .Attribute("formnovalidate", "formnovalidate"));
                }
                form.Cancel();
                form.Submit("common.save");
            }, Address("Format", input.Id, FormatItem), Token, input, _import is null ? FormError : null);
            var body = new List<IUiComponent> { form };
            if (input.Id is { } id) body.Add(new Details(new Summary(new Text("common.remove")), DeleteConfirm(id, input.Version!.Value, FormatItem, false)));
            if (NewItem == FormatItem || Item == FormatItem && Edit is not null || _format is not null)
            {
                page.Content.Add(new Modal(assets, "format-editor", "arr.format.title", body, open: true, returnTo: Address()));
            }

            var import = new Form<ImportCustomFormatsDataV1>(form =>
            {
                form.Select(item => item.TakeoverSourceTypeId, "takeover.source",
                    new[] { TakeoverSourceType.Sonarr, TakeoverSourceType.Radarr }.Select(source => new SelectOption(Number((byte)source), texts["takeover.source." + source])));
                form.Multiline(item => item.Json, "arr.importJson");
                form.Cancel();
                form.Submit("arr.import");
            }, Address("Import"), Token, _import ?? new ImportCustomFormatsDataV1(TakeoverSourceType.Radarr, ""), _import is null ? null : FormError);
            page.Content.Add(new Modal(assets, "format-import", "arr.import", [import], open: _import is not null));
        }

        // Release profiles as Sonarr has them: terms a release must or must not contain, for one media type or every one, a tag and an indexer. A card each with its settings inside.
        private IUiComponent ReleaseSection()
        {
            var content = new List<IUiComponent>();
            if (_releases is not { } result || result.Items.Count == 0)
            {
                content.Add(new Div(new Div(new IconTile(Glyph("shield", 22), UiTone.Neutral), new P(new Text("arr.release.none")).Attribute("role", "status")).Class("list-empty")).Class("list"));
            }
            else
            {
                content.Add(new CardGrid([.. result.Items.Select(ReleaseCard)]).Paged(result.Items.Count, result.PageSize, "releases"));
            }

            if (NewItem == ReleaseItem || _release is { Id: null })
            {
                var input = _release ?? new SaveReleaseProfileDataV1(null, null, "", true, [], [], null, [], null);
                content.Add(new Modal(assets, "release-editor", "arr.release.title", [ReleaseForm(input, true, _release is not null)], open: true, returnTo: Address()));
            }

            return PaneSection("release-profiles", "arr.releaseProfiles", [.. content]);
        }

        private IUiComponent ReleaseCard(ReleaseProfileDetailsV1 item)
        {
            var submitted = _release is { } posted && posted.Id == item.Id;
            var input = submitted ? _release! : new SaveReleaseProfileDataV1(item.Id, item.MediaTypeId, item.Name, item.IsEnabled, item.Required, item.Ignored, item.AcquisitionConnectionId, item.TagIds, item.Version);
            var remove = new A(Glyph("trash", 14), new Text("common.remove")).Class("button button--compact button--warn").Attribute("href", Address(edit: item.Id, item: ReleaseItem) + "&delete=true");
            return new FeatureCard(new IconTile(Glyph("shield", 18), UiTone.Warning), item.Name, null, null, [ReleaseForm(input, false, submitted)], [remove], null, "release-" + Number(item.Id)).Wide();
        }

        private Form<SaveReleaseProfileDataV1> ReleaseForm(SaveReleaseProfileDataV1 input, bool inDialog, bool showError) => new(form =>
        {
            form.Hidden(item => item.Id);
            form.Hidden(item => item.Version);
            form.Text(item => item.Name, "profile.name");
            form.Toggle(item => item.IsEnabled, "admin.providers.enabled");
            form.Select(item => item.MediaTypeId, "profile.appliesTo",
                new[] { new SelectOption("", texts["arr.release.allTypes"]) }.Concat(ReleaseTypes.Select(type => new SelectOption(Number((byte)type), texts[MediaTypeLabel.Key(type, null)]))));
            form.Multiline(item => item.Required, "arr.requiredTerms", help: new FieldHelp(Info: "arr.terms.info"));
            form.Multiline(item => item.Ignored, "arr.ignoredTerms", help: new FieldHelp(Info: "arr.terms.info"));
            form.Select(item => item.AcquisitionConnectionId, "acquisition.role.Search", _indexerChoices);
            PickerPaging(form, input.Id, "indexerChoices", _indexerPage?.Page ?? 1, _indexerPage?.Items.Count == _indexerPage?.PageSize);
            form.Choices(item => item.TagIds, "arr.tags", _tagChoices);
            PickerPaging(form, input.Id, "tagChoices", _tagPage?.Page ?? 1, _tagPage?.Items.Count == _tagPage?.PageSize);
            if (inDialog)
            {
                form.Cancel();
                form.Submit("common.save");
            }
            else form.Save();
        }, Address("Release", input.Id, ReleaseItem), Token, input, showError ? FormError : null);

        // Tags as Sonarr has them: a title carries them, a release profile applies to what carries its own. A card whose one input is the label.
        private IUiComponent TagSection()
        {
            var content = new List<IUiComponent>();
            if (_tags is not { } result || result.Items.Count == 0)
            {
                content.Add(new Div(new Div(new IconTile(Glyph("tag", 22), UiTone.Neutral), new P(new Text("arr.tags.none")).Attribute("role", "status")).Class("list-empty")).Class("list"));
            }
            else
            {
                content.Add(new CardGrid([.. result.Items.Select(TagCard)]).Paged(result.Items.Count, result.PageSize, "tags"));
            }

            if (NewItem == TagItem || _tag is { Id: null })
            {
                var input = _tag ?? new SaveAcquisitionTagDataV1(null, "", null);
                content.Add(new Modal(assets, "tag-editor", "arr.tag.title", [TagForm(input, true, _tag is not null)], open: true, returnTo: Address()));
            }

            return PaneSection("tags", "arr.tags", [.. content]);
        }

        private IUiComponent TagCard(AcquisitionTagDetailsV1 item)
        {
            var submitted = _tag is { } posted && posted.Id == item.Id;
            var remove = new A(Glyph("trash", 14)).Class("button button--icon").TextAttribute("aria-label", "common.remove").TextAttribute("title", "common.remove")
                .Attribute("href", Address(edit: item.Id, item: TagItem) + "&delete=true");
            return new Div(new IconTile(Glyph("tag", 18), UiTone.Accent), TagForm(submitted ? _tag! : new SaveAcquisitionTagDataV1(item.Id, item.Label, item.Version), false, submitted), remove)
                .Class("feature-card tag-card").Attribute("id", "tag-" + Number(item.Id)).Attribute("data-region", "");
        }

        private Form<SaveAcquisitionTagDataV1> TagForm(SaveAcquisitionTagDataV1 input, bool inDialog, bool showError) => new(form =>
        {
            form.Hidden(item => item.Id);
            form.Hidden(item => item.Version);
            form.Text(item => item.Label, "arr.tag");
            if (inDialog)
            {
                form.Cancel();
                form.Submit("common.save");
            }
            else form.Save();
        }, Address("Tag", input.Id, TagItem), Token, input, showError ? FormError : null);

        // The quality sizes as Sonarr's quality definitions: a slider per quality from its minimum over the preferred to its maximum size, the top of the track meaning none.
        // Films (also anime films) and series (also anime series) count megabytes per minute, books per volume and kind; each is one form saved by the page's pill.
        private void DefineQuality(Page page)
        {
            foreach (var type in QualityTypes)
            {
                if (!_sizes.TryGetValue(type, out var data) || data.Rows.Count == 0) continue;
                var books = type == MediaType.Book;
                var limit = SizeLimit(data);
                var ordered = new SaveQualityDefinitionsDataV1(new([.. data.Rows.OrderBy(row => QualityRank(type, row.Quality))]));
                var form = new Form<SaveQualityDefinitionsDataV1>(form =>
                {
                    form.Rows(item => item.Rows, (row, item) =>
                    {
                        row.Hidden(value => value.Quality);
                        row.Hidden(value => value.Version);
                        IUiComponent label = books ? new Text("arr.size." + item.Quality) : new ValueText(item.Quality);
                        if (books) row.SizeRange<decimal, decimal?, decimal?>(label, value => value.MinimumMbPerMinute, null, value => value.MaximumMbPerMinute, limit, "unit.megabytesPerVolume", "arr.size.none");
                        else row.SizeRange(label, value => value.MinimumMbPerMinute, value => value.PreferredMbPerMinute, value => value.MaximumMbPerMinute, limit, "unit.megabytesPerMinute", "arr.size.none");
                        if (books) row.Hidden(value => value.PreferredMbPerMinute);
                    });
                    form.Save();
                }, Address("Sizes") + "&type=" + type, Token, _sizesPosted == type ? data : ordered, _sizesPosted == type ? FormError : null);
                page.Content.Add(PaneSection("quality-" + type.ToString().ToLowerInvariant(), "arr.quality." + type, form));
            }
        }

        // The top of a track: a thousand megabytes (Sonarr's), or above the largest size kept.
        private static decimal SizeLimit(SaveQualityDefinitionsDataV1 data) =>
            Math.Max(1000m, Math.Ceiling(data.Rows.Select(row => Math.Max(row.MinimumMbPerMinute, Math.Max(row.PreferredMbPerMinute ?? 0, row.MaximumMbPerMinute ?? 0))).DefaultIfEmpty(0).Max()) + 1);

        // Video qualities in the order of their list, best first; the kinds of books as they come.
        private static int QualityRank(MediaType type, string quality)
        {
            var order = ProfileQualities.For(type, null);
            var index = order.ToList().FindIndex(item => item.Equals(quality, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }

        private IUiComponent DeleteDialog(long id, long version, string item) =>
            new Modal(assets, "delete-confirm", "common.remove", [new P(new Text("arr.removeConfirm")), DeleteConfirm(id, version, item, true)], open: true, returnTo: Address());

        private IUiComponent DeleteConfirm(long id, long version, string item, bool withCancel)
        {
            return item switch
            {
                FormatItem => Confirm(new DeleteCustomFormatParametersV1(id, version), fields =>
                {
                    fields.Hidden(value => value.CustomFormatId);
                    fields.Hidden(value => value.Version);
                }),
                ReleaseItem => Confirm(new DeleteReleaseProfileParametersV1(id, version), fields =>
                {
                    fields.Hidden(value => value.ReleaseProfileId);
                    fields.Hidden(value => value.Version);
                }),
                _ => Confirm(new DeleteAcquisitionTagParametersV1(id, version), fields =>
                {
                    fields.Hidden(value => value.AcquisitionTagId);
                    fields.Hidden(value => value.Version);
                })
            };

            Form<T> Confirm<T>(T input, Action<Form<T>> fields) where T : BaseData => new(form =>
            {
                fields(form);
                if (withCancel) form.Cancel();
                form.Submit("common.remove");
            }, Address("Delete", id, item), Token, input, FormError);
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

        private static SaveCustomFormatDataV1 FormatInput(long? id, string? name, bool renaming, long? version, List<ConditionRow>? rows) =>
            new(id, name ?? "", renaming, new((rows ?? []).Select(row => new FormatConditionV1(row.Name ?? "", row.Kind, row.Negate,
                row.Required, row.Value, row.ExceptLanguage, row.Resolution, row.Minimum, row.Maximum))), version);

        public async Task<IActionResult> OnPostImportAsync(TakeoverSourceType takeoverSourceTypeId, string? json)
        {
            Tab = DownloadPages.Formats;
            _import = new(takeoverSourceTypeId, json ?? "");
            await importFormats.ExecuteAsync<BaseDataList<SavedCustomFormatV1>>(new SaveCustomFormatParametersV1(), _import, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        // The top of a track is no limit; the limit is the one the page showed, from the sizes kept.
        public async Task<IActionResult> OnPostSizesAsync(string? type, List<SizeRow>? rows)
        {
            Tab = DownloadPages.Quality;
            if (!Enum.TryParse<MediaType>(type, true, out var scope) || !QualityTypes.Contains(scope)) throw new KeyNotFoundException();
            var limit = SizeLimit(new(await sizes.ExecuteAsync<BaseDataList<QualityDefinitionV1>>(new QualityDefinitionsParametersV1(scope), HttpContext.RequestAborted)));
            _sizesPosted = scope;
            _sizes[scope] = new(new((rows ?? []).Select(row => new QualityDefinitionV1(row.Quality ?? "", row.MinimumMbPerMinute,
                row.PreferredMbPerMinute >= limit ? null : row.PreferredMbPerMinute, row.MaximumMbPerMinute >= limit ? null : row.MaximumMbPerMinute, row.Version))));
            await saveSizes.ExecuteAsync<UpdateResult>(new QualityDefinitionsParametersV1(scope), _sizes[scope], HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostReleaseAsync(long? id, string? name, bool isEnabled, MediaType? mediaTypeId, string? required, string? ignored,
            long? acquisitionConnectionId, long[]? tagIds, long? version, bool draft)
        {
            Tab = DownloadPages.Releases;
            _release = new(id, mediaTypeId, name ?? "", isEnabled, Terms(required), Terms(ignored), acquisitionConnectionId, tagIds ?? [], version);
            if (draft) { await LoadDataAsync(); PreparePage(); return Page(); }
            await saveRelease.ExecuteAsync<SavedAcquisitionPolicyV1>(new SaveAcquisitionPolicyParametersV1(), _release, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostTagAsync(long? id, string? label, long? version)
        {
            Tab = DownloadPages.Releases;
            _tag = new(id, label ?? "", version);
            await saveTag.ExecuteAsync<SavedAcquisitionPolicyV1>(new SaveAcquisitionPolicyParametersV1(), _tag, HttpContext.RequestAborted);
            return LocalRedirect(Address());
        }

        public async Task<IActionResult> OnPostDeleteAsync(long version)
        {
            Tab = Item == FormatItem ? DownloadPages.Formats : DownloadPages.Releases;
            var id = Edit ?? throw new KeyNotFoundException();
            switch (Item)
            {
                case FormatItem: await deleteFormat.ExecuteAsync<UpdateResult>(new DeleteCustomFormatParametersV1(id, version), default, HttpContext.RequestAborted); break;
                case ReleaseItem: await deleteRelease.ExecuteAsync<UpdateResult>(new DeleteReleaseProfileParametersV1(id, version), default, HttpContext.RequestAborted); break;
                case TagItem: await deleteTag.ExecuteAsync<UpdateResult>(new DeleteAcquisitionTagParametersV1(id, version), default, HttpContext.RequestAborted); break;
                default: throw new KeyNotFoundException();
            }
            EditText = null;
            ItemText = null;
            return LocalRedirect(Address());
        }

        // The pickers of a release card page through tags and indexers while the card's draft stays.
        private void PickerPaging<T>(Form<T> form, long? id, string key, int current, bool more) where T : BaseData
        {
            if (current > 1) form.Content(PickerButton("common.previous", id, key, current - 1));
            if (more) form.Content(PickerButton("common.next", id, key, current + 1));
        }

        private IUiComponent PickerButton(string labelKey, long? id, string key, int target) =>
            new Button(new Text(labelKey)).Attribute("type", "submit").Attribute("formaction", Address("Release", id, ReleaseItem) + "&draft=true&" + key + "=" + Number(target))
                .Attribute("formnovalidate", "formnovalidate");

        private static string[] Terms(string? value) => value?.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        public sealed record ConditionRow(string? Name, FormatConditionKind Kind, bool Negate, bool Required, string? Value, bool ExceptLanguage, int? Resolution, decimal? Minimum, decimal? Maximum);
        public sealed record SizeRow(string? Quality, decimal MinimumMbPerMinute, decimal? PreferredMbPerMinute, decimal? MaximumMbPerMinute, long Version);
    }
}
