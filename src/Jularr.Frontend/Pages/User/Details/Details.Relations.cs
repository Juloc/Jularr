using System.Globalization;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Media;
using Jularr.Backend.Data.User.Discover.V1;
using Jularr.Backend.Data.User.Media.V1;
using Jularr.Frontend.Framework;

namespace Jularr.Frontend.Pages.User.Details
{
    // The relation type and the words an Owner searches for a title to relate.
    public sealed class WorkRelationSearchData : BaseData
    {
        public WorkRelationSearchData(WorkRelationType workRelationTypeId, string query)
        {
            WorkRelationTypeId = workRelationTypeId;
            Query = query;
        }

        [DataField(typeof(WorkRelationFields), nameof(WorkRelationFields.WorkRelationTypeId))]
        public WorkRelationType WorkRelationTypeId { get; }

        [DataField(typeof(WorkMetadataFields), nameof(WorkMetadataFields.Query))]
        public string Query { get; }
    }

    public sealed partial class DetailsModel
    {
        private const string RelationDialogId = "details-relation-add";
        private IReadOnlyList<WorkRelationV1> _relations = [];
        private WorkRelationSearchData? _relationSearch;
        private IReadOnlyList<DiscoverSuggestionV1>? _relationResults;

        // The series, then the typed relations as a list: every title leads to its Work, and a catalog title the library has nothing of is requested from its row.
        private IUiComponent Related(WorkDetailsV1 work, bool preview)
        {
            var canManage = texts.CanManageRelations();
            var parts = new List<IUiComponent>();
            if (work.FranchiseId is { } franchiseId && work.FranchiseName is { } franchiseName)
            {
                parts.Add(new P(new Span(new Text("details.franchise")), new ValueText(" "), new A(new ValueText(franchiseName)).Attribute("href", "/Franchise/" + franchiseId.ToString(CultureInfo.InvariantCulture)),
                    new ValueText(work.FranchisePosition is { } place ? " · " + place.ToString("0.##", CultureInfo.CurrentCulture) : "")).Class("details-franchise"));
            }

            if (!preview && canManage)
            {
                parts.Add(new Div(Modal.Opener(new Button(new Icon("plus", assets, 16), new Text("details.relation.add")).Soft(), RelationDialogId)).Class("relation-toolbar"));
            }

            var rows = (preview ? _relations.Take(6) : _relations).Select(relation => RelationRow(relation, canManage && !preview)).ToArray();
            parts.Add(new ListPanel("details.related", [new ListColumn("media.title", ListRole.Main), new ListColumn("details.relation.kind", ListRole.Status), new ListColumn("media.type", ListRole.Data, 1),
                new ListColumn("table.status", ListRole.Data), new ListColumn("common.actions", ListRole.Actions)], rows).Modifier("relation-list"));
            return new Region("details-relations", [.. parts]);
        }

        // Cover, title and year; what the title is to this Work with its source in the tip; the media type; what the library has of it; and the actions.
        private ListRow RelationRow(WorkRelationV1 relation, bool canManage)
        {
            var address = "/Details/" + relation.RelatedWorkId.ToString(CultureInfo.InvariantCulture);
            var cover = MediaImage.Or(assets, relation.PosterUrl, "", 92, 138);
            var title = new A(new Truncated(relation.Title)).Class("relation__title").Attribute("href", address);
            var year = new Span(new ValueText(relation.ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? ""));
            var identity = new ListIdentity(new IconTile(cover, UiTone.Accent), title, year);

            var kind = new StateChip(new Text("details.relation." + relation.WorkRelationTypeId), UiTone.Accent, new Icon(KindIcon(relation.WorkRelationTypeId), assets, 14))
                .Attribute("title", string.Format(CultureInfo.CurrentCulture, texts["details.relation.source"], SourceName(relation.SourceKey)));
            var typeLabel = new Tag(new Text(MediaTypeLabel.Key(relation.MediaTypeId, relation.MediaSubtypeId)));
            var state = Availability(relation);

            var actions = new List<IUiComponent>();
            if (relation.AvailabilityStateId is null && texts.CanRequest())
            {
                actions.Add(new PostForm(Address(query => query["handler"] = "RequestRelated"), _csrf, [("relatedWorkId", relation.RelatedWorkId.ToString(CultureInfo.InvariantCulture))],
                    new Button(new Text("details.relation.request")).Compact().Soft().Submit()));
            }

            if (canManage && relation.WorkRelationId is { } relationId)
            {
                actions.Add(new PostForm(Address(query => query["handler"] = "RemoveRelation"), _csrf, [("workRelationId", relationId.ToString(CultureInfo.InvariantCulture))],
                    new Button(new Icon("trash", assets, 16)).IconOnly(texts["details.relation.remove"]).Submit()));
            }

            return new ListRow([identity, kind, typeLabel, state, new Div([.. actions]).Class("list-actions")], [ListPanel.Fact("media.type", new Text(MediaTypeLabel.Key(relation.MediaTypeId, relation.MediaSubtypeId))),
                ListPanel.Fact("table.status", state)]);
        }

        // What the library has of the related Work, in the card's states; a catalog title the library has nothing of says so.
        private IUiComponent Availability(WorkRelationV1 relation)
        {
            if (relation.AvailabilityStateId is not { } state)
            {
                return new StateChip(new Text("details.relation.notInLibrary"), UiTone.Neutral, new Icon("cloud", assets, 14));
            }

            var (tone, icon) = state switch
            {
                MediaAvailabilityState.Available or MediaAvailabilityState.ProfileAudio => (UiTone.Success, "check"),
                MediaAvailabilityState.Partial or MediaAvailabilityState.PartialOtherAudio or MediaAvailabilityState.PartialPreferredSubtitles => (UiTone.Warning, "layers"),
                MediaAvailabilityState.PreferredSubtitles => (UiTone.Info, "subtitles"),
                MediaAvailabilityState.Paused => (UiTone.Neutral, "pause"),
                MediaAvailabilityState.OtherAudio => (UiTone.Info, "subtitles"),
                MediaAvailabilityState.Downloading => (UiTone.Info, "download"),
                MediaAvailabilityState.Searching => (UiTone.Info, "search"),
                MediaAvailabilityState.Requested => (UiTone.Accent, "requests"),
                _ => (UiTone.Neutral, "cloud")
            };
            var label = state switch
            {
                MediaAvailabilityState.Missing => texts["details.relation.missing"],
                MediaAvailabilityState.Partial or MediaAvailabilityState.PartialOtherAudio or MediaAvailabilityState.PartialPreferredSubtitles => string.Format(CultureInfo.CurrentCulture, texts["media.availability.tag." + state], relation.AvailableUnits, relation.MonitoredUnits),
                _ => texts["media.availability.tag." + state]
            };
            return new StateChip(new ValueText(label), tone, new Icon(icon, assets, 14));
        }

        private static string KindIcon(WorkRelationType type) => type switch
        {
            WorkRelationType.Sequel => "arrow-right",
            WorkRelationType.Prequel or WorkRelationType.MainStory or WorkRelationType.SourceMaterial => "arrow-left",
            WorkRelationType.Adaptation => "film",
            WorkRelationType.Alternative => "copy",
            WorkRelationType.Summary => "list",
            WorkRelationType.Other => "link",
            _ => "layers"
        };

        // The catalogs and providers are brand names, not words of the interface; only a relation made by hand is named in words.
        private string SourceName(string key) => key switch
        {
            "anilist" => "AniList",
            "tmdb" => "TMDB",
            "tvdb" => "TVDB",
            "hardcover" => "Hardcover",
            "openlibrary" => "Open Library",
            "musicbrainz" => "MusicBrainz",
            "igdb" => "IGDB",
            _ => texts["details.relation.source.manual"]
        };

        // The Owner finds a Work by its words and says what it is to this Work; the matches come with an Add button each.
        private IUiComponent RelationDialog()
        {
            var types = Enum.GetValues<WorkRelationType>().Select(type => new SelectOption(((byte)type).ToString(CultureInfo.InvariantCulture), texts["details.relation." + type]));
            var content = new List<IUiComponent>
            {
                new Form<WorkRelationSearchData>(form =>
                {
                    form.Select(data => data.WorkRelationTypeId, "details.relation.kind", types);
                    form.Text(data => data.Query, "details.relation.find");
                    form.Submit("details.relation.search");
                }, Address(query => query["handler"] = "SearchRelation"), _csrf, _relationSearch, _relationSearch is null || _relationResults is null ? null : FormError)
            };

            if (_relationSearch is { } search && _relationResults is { } found)
            {
                var rows = found.Select(item => new ListRow([new ListIdentity(new IconTile(MediaImage.Or(assets, item.PosterUrl, "", 92, 138), UiTone.Accent),
                    new Truncated(item.Title), new Span(new ValueText(item.ReleaseYear?.ToString(CultureInfo.InvariantCulture) ?? ""))), new Div(AddForm(item, search.WorkRelationTypeId)).Class("list-actions")])).ToArray();
                content.Add(new ListPanel("details.relation.results", [new ListColumn("media.title", ListRole.Main), new ListColumn("common.actions", ListRole.Actions)], rows,
                    new Div(new P(new Text("details.relation.none")))).Modifier("relation-results"));
            }

            return new Modal(assets, RelationDialogId, "details.relation.dialog", [new Div([.. content]).Class("relation-dialog")], _relationSearch is not null);
        }

        // One match as the Work it is.
        private IUiComponent AddForm(DiscoverSuggestionV1 item, WorkRelationType type) =>
            new PostForm(Address(query => query["handler"] = "AddRelation"), _csrf,
                [("workRelationTypeId", ((byte)type).ToString(CultureInfo.InvariantCulture)), ("relatedWorkId", item.WorkId.ToString(CultureInfo.InvariantCulture))],
                new Button(new Icon("plus", assets, 14), new Text("details.relation.pick")).Compact().Soft().Submit());
    }
}
