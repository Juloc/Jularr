using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Enums.Media;
using Jularr.Backend.Data.Fields.Acquisition;
using Jularr.Backend.Data.Fields.Library;
using Jularr.Backend.Data.Fields.Media;

namespace Jularr.Backend.Data.Admin.Media.V1
{
    public sealed class MediaImportParametersV1 : BaseParameters
    {
    }

    // What a person decided for one row of the preview.
    public sealed class MediaImportChoiceV1 : BaseData
    {
        public MediaImportChoiceV1(int index, bool? include, string? choice, string? query, MediaImportActionType? action, long? acquisitionProfileId, long? libraryRootId)
        {
            Index = index;
            Include = include;
            Choice = choice;
            Query = query;
            Action = action;
            AcquisitionProfileId = acquisitionProfileId;
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Index))]
        public int Index { get; }

        // A row left out is not touched; without a choice every row that has something to do is included.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Include), false)]
        public bool? Include { get; }

        // The provider entry chosen for an open row, as "namespace:id".
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Choice), false)]
        public string? Choice { get; }

        // Another search of the provider for an open row.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Query), false)]
        public string? Query { get; }

        // Without one the row follows the whole list.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Action), false)]
        public MediaImportActionType? Action { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }
    }

    // The quality profile and library for every title of one kind, such as movie or manga.
    public sealed class MediaImportDefaultV1 : BaseData
    {
        public MediaImportDefaultV1(string key, long? acquisitionProfileId, long? libraryRootId)
        {
            Key = key;
            AcquisitionProfileId = acquisitionProfileId;
            LibraryRootId = libraryRootId;
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Key))]
        public string Key { get; }

        [DataField(typeof(AcquisitionProfileFields), nameof(AcquisitionProfileFields.Id), false)]
        public long? AcquisitionProfileId { get; }

        [DataField(typeof(LibraryRootFields), nameof(LibraryRootFields.Id), false)]
        public long? LibraryRootId { get; }
    }

    public sealed class MediaImportDataV1 : BaseData
    {
        public MediaImportDataV1(UploadedFile? file, string? json, MediaImportActionType action, BaseDataList<MediaImportDefaultV1>? defaults = null, BaseDataList<MediaImportChoiceV1>? choices = null)
        {
            File = file;
            Json = json;
            Action = action;
            Defaults = defaults ?? new([]);
            Choices = choices ?? new([]);
        }

        // The list as a file, on its first step.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.File), false)]
        public UploadedFile? File { get; }

        // The list as text, pasted or carried on from the first step.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Json), false)]
        public string? Json { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Action))]
        public MediaImportActionType Action { get; }

        public BaseDataList<MediaImportDefaultV1> Defaults { get; }
        public BaseDataList<MediaImportChoiceV1> Choices { get; }

        public override void Validate()
        {
            base.Validate();
            if (File is null && string.IsNullOrWhiteSpace(Json)) throw new DataValidationException(nameof(Json), "required");
            if (Defaults.Count > 16 || Choices.Count > MediaImportFields.MaximumItems) throw new DataValidationException(nameof(Choices), "out_of_range");
            Defaults.Validate(nameof(Defaults));
            Choices.Validate(nameof(Choices));
            if (Defaults.Any(item => string.IsNullOrEmpty(item.Key))) throw new DataValidationException(nameof(Defaults), "required");
            if (Defaults.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != Defaults.Count) throw new DataValidationException(nameof(Defaults), "duplicate_value");
            if (Choices.Select(item => item.Index).Distinct().Count() != Choices.Count) throw new DataValidationException(nameof(Choices), "duplicate_value");
        }
    }

    // A provider entry that may be what an open row means.
    public sealed class MediaImportCandidateV1 : BaseResponse
    {
        public MediaImportCandidateV1(string choice, string title, int? releaseYear, string? creator, string source, long? workId, bool isSuggested)
        {
            Choice = choice;
            Title = title;
            ReleaseYear = releaseYear;
            Creator = creator;
            Source = source;
            WorkId = workId;
            IsSuggested = isSuggested;
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Choice))]
        public string Choice { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Title))]
        public string Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Creator), false)]
        public string? Creator { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Source))]
        public string Source { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        // The one candidate that carries the whole title and the year or author of the row; it is only proposed, never taken.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Flag))]
        public bool IsSuggested { get; }
    }

    // One title of the list: what the file says, what Jularr finds and what would happen.
    public sealed class MediaImportRowV1 : BaseResponse
    {
        public MediaImportRowV1(int index, string typeKey, MediaType? mediaTypeId, MediaSubtype? mediaSubtypeId, string? title, int? releaseYear, string? creator, MediaImportStatusType statusTypeId, string? problem, string? problemDetail,
            long? workId, string? matchTitle, int? matchYear, string? matchSource, string? choice, bool hasTarget, bool canMonitor, bool isIncluded, string? query, int? duplicateOf,
            BaseDataList<MediaImportCandidateV1> candidates, MediaImportActionType action)
        {
            Index = index;
            TypeKey = typeKey;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
            Title = title;
            ReleaseYear = releaseYear;
            Creator = creator;
            StatusTypeId = statusTypeId;
            Problem = problem;
            ProblemDetail = problemDetail;
            WorkId = workId;
            MatchTitle = matchTitle;
            MatchYear = matchYear;
            MatchSource = matchSource;
            Choice = choice;
            HasTarget = hasTarget;
            CanMonitor = canMonitor;
            IsIncluded = isIncluded;
            Query = query;
            DuplicateOf = duplicateOf;
            Candidates = candidates;
            Action = action;
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Index))]
        public int Index { get; }

        // The kind of title the file names, such as movie or manga.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Key))]
        public string TypeKey { get; }

        // Not known for an entry whose type is wrong.
        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId), false)]
        public MediaType? MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Title), false)]
        public string? Title { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? ReleaseYear { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Creator), false)]
        public string? Creator { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.StatusTypeId))]
        public MediaImportStatusType StatusTypeId { get; }

        // Why the entry cannot be used, as a key of the problem texts.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Problem), false)]
        public string? Problem { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.ProblemDetail), false)]
        public string? ProblemDetail { get; }

        // The title in the library, if there is one.
        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }

        // The title as the library or the provider names it.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Title), false)]
        public string? MatchTitle { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? MatchYear { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Source), false)]
        public string? MatchSource { get; }

        // The provider entry that would be added, as "namespace:id".
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Choice), false)]
        public string? Choice { get; }

        // The title already has a target to search for.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Flag))]
        public bool HasTarget { get; }

        // Games take their platform on the title page.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Flag))]
        public bool CanMonitor { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Include))]
        public bool IsIncluded { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Query), false)]
        public string? Query { get; }

        // The first row of the file that names the same title.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Index), false)]
        public int? DuplicateOf { get; }

        public BaseDataList<MediaImportCandidateV1> Candidates { get; }

        // What the row does with its title: its own choice, else the list's.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Action))]
        public MediaImportActionType Action { get; }

        public override void Validate()
        {
            base.Validate();
            Candidates.Validate(nameof(Candidates));
        }
    }

    // A quality profile or library a row may choose.
    public sealed class MediaImportOptionV1 : BaseResponse
    {
        public MediaImportOptionV1(long id, string name, MediaType mediaTypeId, MediaSubtype? mediaSubtypeId)
        {
            Id = id;
            Name = name;
            MediaTypeId = mediaTypeId;
            MediaSubtypeId = mediaSubtypeId;
        }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id))]
        public long Id { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Name))]
        public string Name { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaTypeId))]
        public MediaType MediaTypeId { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.MediaSubtypeId))]
        public MediaSubtype? MediaSubtypeId { get; }
    }

    public sealed class MediaImportPreviewV1 : BaseResponse
    {
        public MediaImportPreviewV1(string json, BaseDataList<MediaImportRowV1> rows, BaseDataList<MediaImportOptionV1> profiles, BaseDataList<MediaImportOptionV1> libraries, int unsearched)
        {
            Json = json;
            Rows = rows;
            Profiles = profiles;
            Libraries = libraries;
            Unsearched = unsearched;
        }

        // The list as it is carried through the steps.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Json))]
        public string Json { get; }

        public BaseDataList<MediaImportRowV1> Rows { get; }
        public BaseDataList<MediaImportOptionV1> Profiles { get; }
        public BaseDataList<MediaImportOptionV1> Libraries { get; }

        // Open rows whose search waits for another preview.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Count))]
        public int Unsearched { get; }

        public override void Validate()
        {
            base.Validate();
            Rows.Validate(nameof(Rows));
            Profiles.Validate(nameof(Profiles));
            Libraries.Validate(nameof(Libraries));
        }
    }

    public sealed class MediaImportResultRowV1 : BaseResponse
    {
        public MediaImportResultRowV1(int index, string? title, MediaImportOutcomeType outcomeTypeId, bool searches, long? workId)
        {
            Index = index;
            Title = title;
            OutcomeTypeId = outcomeTypeId;
            Searches = searches;
            WorkId = workId;
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Index))]
        public int Index { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Title), false)]
        public string? Title { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.OutcomeTypeId))]
        public MediaImportOutcomeType OutcomeTypeId { get; }

        // The title is searched for at once.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Flag))]
        public bool Searches { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.Id), false)]
        public long? WorkId { get; }
    }

    public sealed class MediaImportResultV1 : BaseResponse
    {
        public MediaImportResultV1(BaseDataList<MediaImportResultRowV1> rows, string? remainingJson)
        {
            Rows = rows;
            RemainingJson = remainingJson;
        }

        public BaseDataList<MediaImportResultRowV1> Rows { get; }

        // The rows a run left for the next one, as a list of their own.
        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Json), false)]
        public string? RemainingJson { get; }

        public override void Validate()
        {
            base.Validate();
            Rows.Validate(nameof(Rows));
        }
    }

    // A public-provider edition comparison is read-only and permission-gated.
    public sealed class MediaImportEditionParametersV1 : BaseParameters
    {
        public MediaImportEditionParametersV1(string workId) => WorkId = workId;

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Key))]
        public string WorkId { get; }

        public override void Validate()
        {
            base.Validate();
            if (!System.Text.RegularExpressions.Regex.IsMatch(WorkId, @"\AOL[0-9]{1,12}W\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new DataValidationException(nameof(WorkId), "invalid_value");
        }
    }

    public sealed class MediaImportEditionV1 : BaseResponse
    {
        public MediaImportEditionV1(string? title, string? language, string? format, string? publisher, int? year, string? isbn)
        {
            Title = title;
            Language = language;
            Format = format;
            Publisher = publisher;
            Year = year;
            Isbn = isbn;
        }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Title), false)]
        public string? Title { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Key), false)]
        public string? Language { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Title), false)]
        public string? Format { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Title), false)]
        public string? Publisher { get; }

        [DataField(typeof(WorkFields), nameof(WorkFields.ReleaseYear))]
        public int? Year { get; }

        [DataField(typeof(MediaImportFields), nameof(MediaImportFields.Key), false)]
        public string? Isbn { get; }
    }

    public sealed class MediaImportEditionsV1 : BaseResponse
    {
        public MediaImportEditionsV1(BaseDataList<MediaImportEditionV1> editions) => Editions = editions;
        public BaseDataList<MediaImportEditionV1> Editions { get; }
        public override void Validate()
        {
            base.Validate();
            Editions.Validate(nameof(Editions));
        }
    }

    // One list in the shape the importer reads.
    public static class MediaImportExample
    {
        public static string Json { get; } = string.Join('\n',
            "{",
            "  \"version\": 1,",
            "  \"items\": [",
            "    { \"type\": \"movie\", \"ids\": { \"tmdb\": \"603\", \"imdb\": \"tt0133093\" }, \"title\": \"The Matrix\", \"year\": 1999 },",
            "    { \"type\": \"series\", \"ids\": { \"tmdb\": \"1396\", \"tvdb\": \"81189\" }, \"title\": \"Breaking Bad\" },",
            "    { \"type\": \"anime\", \"ids\": { \"anilist\": \"16498\" }, \"title\": \"Attack on Titan\" },",
            "    { \"type\": \"book\", \"ids\": { \"isbn\": \"978-0-261-10235-4\", \"openlibrary\": \"OL27448W\" }, \"title\": \"The Lord of the Rings\", \"author\": \"J. R. R. Tolkien\" },",
            "    { \"type\": \"manga\", \"ids\": { \"anilist\": \"30002\" }, \"title\": \"Berserk\" },",
            "    { \"type\": \"lightnovel\", \"title\": \"Sword Art Online\", \"author\": \"Reki Kawahara\" },",
            "    { \"type\": \"music\", \"ids\": { \"musicbrainz\": \"f5093c06-23e3-404f-aeaa-40f72885ee3a\" }, \"title\": \"The Dark Side of the Moon\", \"artist\": \"Pink Floyd\" },",
            "    { \"type\": \"game\", \"ids\": { \"igdb\": \"1942\" }, \"title\": \"The Witcher 3: Wild Hunt\", \"year\": 2015 }",
            "  ]",
            "}",
            "");

        public static string FileName => "jularr-media-list.json";
    }
}
