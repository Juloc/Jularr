namespace Jularr.DatabaseCutover.Target;

public enum AccountRole : byte
{
    Owner = 1,
    User = 2,
    MediaManager = 3
}

public enum MediaType : byte
{
    Movie = 1,
    Series = 2,
    Book = 3,
    Manga = 4,
    LightNovel = 5,
    Music = 6,
    Game = 7
}

public enum ProgressPositionType : byte
{
    Time = 1,
    Reading = 2,
    Game = 3
}

public enum ImageTargetKind : byte
{
    Work = 1,
    WorkChapter = 2,
    MediaChapter = 3
}

public enum MediaSegmentType : byte
{
    Intro = 1,
    Recap = 2,
    Outro = 3,
    Credits = 4,
    Preview = 5
}

public enum OperationStatus : byte
{
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
    Interrupted = 6
}

public enum AcquisitionRuleField : byte
{
    RawTitle = 0,
    ReleaseGroup = 1,
    Source = 2,
    Resolution = 3,
    VideoCodec = 4,
    BitDepth = 5,
    HdrFormat = 6,
    AudioCodec = 7,
    AudioLanguage = 8,
    SubtitleLanguage = 9,
    DualAudio = 10,
    MultiAudio = 11,
    Proper = 12,
    Repack = 13,
    Indexer = 14
}

public enum AcquisitionRuleMatch : byte
{
    Equals = 0,
    Contains = 1,
    Regex = 2
}

public enum AcquisitionRuleEffect : byte
{
    Prefer = 0,
    Avoid = 1,
    Require = 2,
    Reject = 3,
    Info = 4
}

public enum LearningUnitKind : byte
{
    Word = 1,
    Sentence = 2,
    Script = 3
}

public enum LearningVariantRole : byte
{
    Primary = 1,
    Meaning = 2
}

public enum LearningVariantSource : byte
{
    Manual = 1,
    Term = 2,
    Dictionary = 3,
    ScriptCatalog = 4
}

public enum LearningCardMode : byte
{
    Recognition = 1,
    Production = 2,
    Listening = 3,
    Writing = 4
}

public enum LearningCardState : byte
{
    Known = 1,
    Learning = 2,
    Saved = 3,
    Ignored = 4,
    Suspended = 5
}

public enum LearningCardReviewRating : byte
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4
}

public enum LearningMode : byte
{
    Off = 0,
    LanguageTools = 1,
    Study = 2,
    Custom = 3
}

public enum LearningCapability : byte
{
    LanguageLookup = 1,
    ReadingAids = 2,
    Translation = 3,
    AiExplanations = 4,
    Vocabulary = 5,
    Reviews = 6,
    SentencePractice = 7,
    ScriptTrainer = 8,
    Progress = 9,
    HomeWidget = 10,
    ContentMetrics = 11,
    PreparationSuggestions = 12,
    PlayerTools = 13,
    ReaderTools = 14
}

public enum LearningMediaScope : byte
{
    Anime = 1,
    Novel = 2,
    Book = 3,
    Manga = 4
}

public enum EventCategory : byte
{
    DownloadGrabbed = 1,
    DownloadFailed = 2,
    ImportCompleted = 3,
    ImportFailed = 4,
    ReleaseAvailable = 5,
    RequestApproved = 6,
    RequestDenied = 7,
    StorageProblem = 8
}

public enum EventAudience : byte
{
    Profile = 1,
    Admin = 2
}

public enum EventSeverity : byte
{
    Info = 1,
    Warning = 2,
    Critical = 3
}

public enum NotificationChannel : byte
{
    InApp = 1,
    Push = 2,
    Email = 3
}

public enum NotificationTiming : byte
{
    Immediate = 1,
    Digest = 2
}
