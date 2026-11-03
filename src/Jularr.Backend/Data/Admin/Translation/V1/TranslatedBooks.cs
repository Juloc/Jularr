using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Reading;
using Jularr.Backend.Data.Fields.Translation;

namespace Jularr.Backend.Data.Admin.Translation.V1
{
    public sealed class TranslatedBooksParametersV1 : BaseParameters
    {
        public TranslatedBooksParametersV1(int page = 1, int pageSize = 25)
        {
            Page = page;
            PageSize = pageSize;
        }

        public int Page { get; }
        public int PageSize { get; }
    }

    // One book the server translated (part of) into one language: its sections with a server text for every block (from any model; earlier versions of a
    // model count), and every model that translated some of it with its own sections done and those still showing an earlier version, out of the book's
    // sections with text. Models, Done and Stale go together, most done first.
    public sealed class TranslatedBookV1 : BaseResponse
    {
        public TranslatedBookV1(long documentId, long workId, string title, string? volume, string language, int sections, int translated, string[] models, int[] done, int[] stale)
        {
            DocumentId = documentId;
            WorkId = workId;
            Title = title;
            Volume = volume;
            Language = language;
            Sections = sections;
            Translated = translated;
            Models = models;
            Done = done;
            Stale = stale;
        }

        [DataField(typeof(ReadingFields), nameof(ReadingFields.DocumentId))]
        public long DocumentId { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.WorkId))]
        public long WorkId { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Title))]
        public string Title { get; }

        // The volume's number when the book is one volume of a series.
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Volume))]
        public string? Volume { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Language))]
        public string Language { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Count))]
        public int Sections { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Count))]
        public int Translated { get; }

        // Provider-qualified model ids such as "ollama:translategemma:12b".
        [DataField(typeof(TranslationFields), nameof(TranslationFields.Models))]
        public string[] Models { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Counts))]
        public int[] Done { get; }

        [DataField(typeof(TranslationFields), nameof(TranslationFields.Counts))]
        public int[] Stale { get; }

        // Every section is translated, so the translated EPUB downloads.
        public bool IsComplete() => Sections > 0 && Translated >= Sections;
    }
}
