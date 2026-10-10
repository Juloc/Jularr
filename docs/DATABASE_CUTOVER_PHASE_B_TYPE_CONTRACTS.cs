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

public enum AcquisitionIndexerType : byte
{
    Prowlarr = 1,
    Newznab = 2
}

public enum AcquisitionKind : byte
{
    Anime = 1,
    Manga = 2,
    LightNovel = 3,
    Book = 4,
    Movie = 5,
    Tv = 6,
    Audiobook = 7,
    Music = 8
}

public enum AcquisitionRequestStatus : byte
{
    Pending = 1,
    Approved = 2,
    Searching = 3,
    Downloading = 4,
    Importing = 5,
    Completed = 6,
    Rejected = 7,
    Failed = 8
}

public enum CurriculumExerciseKind : byte
{
    Presentation = 1,
    MultipleChoice = 2,
    Matching = 3,
    Cloze = 4,
    Ordering = 5,
    ShortAnswer = 6
}

public enum CurriculumExercisePhase : byte
{
    Orient = 1,
    Introduce = 2,
    Example = 3,
    GuidedPractice = 4,
    IndependentRetrieval = 5,
    Apply = 6,
    Checkpoint = 7,
    Summary = 8
}

public enum CurriculumObjectiveSkill : byte
{
    Vocabulary = 1,
    Grammar = 2,
    Reading = 3,
    Listening = 4,
    Script = 5,
    Production = 6,
    Writing = 7,
    Speaking = 8
}

public enum LearnerProgressStatus : byte
{
    NotStarted = 1,
    InProgress = 2,
    Completed = 3,
    Skipped = 4
}

public enum LearnerProgressSkipReason : byte
{
    Optional = 1,
    CapabilityUnavailable = 2
}

public enum LearnerExerciseOutcome : byte
{
    Completed = 1,
    Correct = 2,
    Incorrect = 3,
    Partial = 4
}

public enum LearningActivityKind : byte
{
    Lesson = 1,
    Review = 2,
    VocabularyPractice = 3,
    SentencePractice = 4,
    ScriptPractice = 5,
    MediaPractice = 6
}

public enum LearningSessionEndReason : byte
{
    Completed = 1,
    Exited = 2,
    Abandoned = 3
}

public enum LearningActivityEventKind : byte
{
    ExerciseCompleted = 1,
    ReviewCompleted = 2,
    LessonCompleted = 3,
    PracticeItemCompleted = 4,
    SessionCompleted = 5
}

public enum GameReleaseFileRole : byte
{
    Primary = 1,
    Disc = 2,
    Track = 3,
    Auxiliary = 4
}

public enum MediaSegmentSource : byte
{
    Manual = 1,
    Imported = 2,
    Provider = 3,
    Detector = 4
}

public enum MediaTrackType : byte
{
    Audio = 1,
    Subtitle = 2,
    Video = 3
}

public enum WorkRelationType : byte
{
    Prequel = 1,
    Sequel = 2,
    Parent = 3,
    SideStory = 4,
    Alternative = 5,
    SpinOff = 6,
    Adaptation = 7,
    Source = 8,
    Summary = 9,
    FullStory = 10,
    Character = 11,
    Contains = 12,
    Remake = 13,
    Other = 14
}

public enum ImageType : byte
{
    Cover = 1,
    Banner = 2,
    Poster = 3,
    Backdrop = 4,
    Logo = 5,
    Screenshot = 6,
    TitleScreen = 7
}

public enum MediaAssetType : byte
{
    Video = 1,
    Audio = 2,
    Ebook = 3,
    ComicArchive = 4,
    Subtitle = 5,
    Image = 6,
    GameBinary = 7
}

public enum MediaDetectionType : byte
{
    AudioFingerprint = 1
}

public enum MediaDetectionStatus : byte
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4
}

public enum WorkCreditRole : byte
{
    Cast = 1,
    Crew = 2,
    Author = 3,
    Narrator = 4,
    Illustrator = 5
}

public enum WorkFactType : byte
{
    FirstPublishedOn = 1,
    RuntimeMs = 2,
    OriginalLanguage = 3,
    CommunityRating = 4,
    CommunityRatingCount = 5,
    Certification = 6,
    CertificationCountry = 7,
    Studios = 8,
    ProductionCountries = 9
}

public enum WorkMediaClassification : byte
{
    Anime = 1
}
