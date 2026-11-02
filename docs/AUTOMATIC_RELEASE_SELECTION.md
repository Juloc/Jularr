# Automatic release selection

Status: **active**. Owner direction of #865 (one engine, identity before score, every decision named) and the approved book
recognition of #1706 (Owner decisions of 29 October 2026). Manual search, automatic search and the Search All queue use the same
evaluator; there is no second scorer.

## Order of a decision

`ReleaseSelectionEngine` ([ReleaseSelection.cs](../src/Jularr.Backend/Logic/Acquisition/ReleaseSelection.cs)) judges every
candidate in this order; a later step never repairs an earlier one:

1. Safety: no download link, a failed or blocked release, an unmonitored edition language.
2. Identity from the media type's judge: Exact, Strong, Ambiguous or Conflict. Conflict rejects.
3. Hard rules of the media type (`SelectionCandidate.Rejection`): a book release of another kind, a book size outside its limits, a
   blocked release group.
4. The profile: allowed qualities, sizes, release profile terms and the minimum custom format score.
5. Marks for a person (`SelectionCandidate.Flag`, manual search only: a size outside the limits, a blocked release group), an
   unrequested language, no upgrade of the present file, an ambiguous identity: Manual review.
6. Everything else is Eligible; Auto takes the best Eligible candidate at once (no wait times).

The score of a row is the sum of its points: the profile's custom formats and its release group's rating plus the media type's own
rules (`ContextPoints`).
Each rule names its points; the manual search shows the sum and every rule on hover. A rejected row keeps its points and names
the rule that rejected it.

## Fixed tie-break order

Equal input gives the same ranking whatever order the indexers answered in:

1. a complete book before one part of it; for books the requested language, then (with any fallback) the quality of
   other languages;
2. indexer priority;
3. decision (Eligible, Manual review, Rejected);
4. quality rank of the profile;
5. custom format score;
6. language rank of the profile;
7. points of the media type's rules;
8. identity confidence;
9. coverage (wanted units covered, minus 10 points for every unit beyond them);
10. bounded reliability of group and indexer;
11. the later posting;
12. distance to the preferred size (video);
13. the candidate's stable identifier.

## Release groups

Each media type keeps one list of release groups (`ReleaseGroups`, migration `0028`; [ReleaseGroupLogic.cs](../src/Jularr.Backend/Logic/Acquisition/ReleaseGroupLogic.cs)).
Every manual or automatic search adds the groups its results name (the video parser's group; for books and manga the last named tag, see
below), counts each once per search and keeps when it last saw it; no extra provider call is made. A name with several groups
("danke-Empire, Ushi", also joined by `&`, `+` or `/`) is several groups for the list, the rating and the Group column. A group can also be
typed by hand (Admin, Acquisition, Profiles, Release groups). A name is trimmed and kept once per media type in any case. A rating saves on
click and refreshes the figures with the list; the chosen segment clicked again takes it back (not rated).

A rating (Good, Bad, Blocked; none is not rated) applies to every profile of the media type and is one more named rule of the score
(`ReleaseScorer.GroupRating`), not a tie-break:

- Good and Bad add the profile's points, "Group X (good)" and "Group X (bad)", to the format score, once for each rated group of the name. The points live in the profile
  definition (`GoodGroupScore` 0 to +1,000,000, default +50, `BadGroupScore` -1,000,000 to 0, default -50; the profile page's Release groups section). The minimum format score counts the custom formats
  alone, so a bad group lowers the score but is not rejected for it.
- Blocked (any group of the name) rejects the release in an automatic search ("Group blocked") and only marks it in a manual search, where it
  can still be taken.
- An eligible release names its group's rating as its reason; the manual search shows the group in its own column.

## Books and light novels

[BookReleaseJudge.cs](../src/Jularr.Backend/Logic/Acquisition/BookReleaseJudge.cs) owns the book side.

**Name.** `ReadName` is a pure function of the release name (`BookReleaseName`): kind (e-book, audiobook, manga/comic, magazine),
format, a single volume or a range (`v01-23`, `Vol 01-24`, `Volume 3`), edition words in a fixed order (Digital, Web Novel,
Compilation, Complete, Illustrated), publisher and group (a known publisher or imprint such as Seven Seas, Yen Press, Kodansha, Square Enix or J-Novel Club is always the
publisher, never the group; of the other named tags the last is the group, the one before it the publisher; a dotted scene name ends in its group), the first year and the languages (words, catalog tags and a code alone in brackets such as `(EN)`).
An audiobook says so or names an audio file (M4B, M4A, MP3, FLAC; "unabridged" or "narrated" without an e-book format); a comic names
its file, says "manga" or "comic", is a `Digital-Compilation` or follows the comic naming `Title v01 (2015) (Digital) (Group)`.

**Identity.** The request's title (or an edition title) must appear. A named author makes it Exact. Without the author the title
alone is Strong unless the name adds words of its own (another book of the series, a spin-off: Ambiguous with those words). A web
novel is another text of the story: Ambiguous unless the request is the web novel. A missing author never makes a clean title
match ambiguous. Guides and summaries stay a Conflict.

**Kind.** An e-book or light-novel request rejects an audiobook, a manga/comic and a magazine, each with its own reason.
Audiobooks become their own media type later.

**Points.** Title matches 20, author named 10, format EPUB 40, AZW3 30, MOBI 30, PDF 10, a format the name does not state 0,
illustrated edition 15.

**Sizes per volume.** Quality sizes of the book media type hold one row per kind in megabytes per volume (Admin, Profiles, Sizes,
scope Book): e-book 0 to 150 MB, illustrated 0 to 300 MB, manga volume 0 to 700 MB (migration `0025`; manga raised from the proposed
300 MB because digital manga volumes of the corpus measure 340 to 630 MB). The size is divided by the volumes the name counts; an
unnumbered release counts one volume for a book and an unknown number for a light novel series, which is checked only when it fits as
a whole and is otherwise marked "Volumes unknown". Out of range is rejected with the reason and both sizes in an automatic search
and only marked (Manual review, still grabbable) in a manual search. Audiobooks and magazines have no limit.

**Packs.** Book and light-novel targets are searched as the whole Work, so a pack covers it; at equal quality and points a single
volume beats a pack, because every further volume costs coverage. The table shows the volumes of every row. Volume-scoped checks
(a pack that misses the wanted volume) are done for Manga by `MangaReleaseJudge`.

## Tests

`BookReleaseCorpusTests` reads the golden corpus
[book-releases.tsv](../tests/Jularr.Backend.Tests/Fixtures/ReleaseNames/book-releases.tsv): the eleven Nyaa answers for
"Mushoku Tensei: Jobless Reincarnation" plus Sonarr, Radarr, Readarr and Lidarr style names, each with the expected parse, size
mark, decision, reason and score; the same corpus in shuffled order gives the same ranking. Identity regressions:
`BookReleaseIdentityTests`, `ReleaseSelectionTests`, `BookTests`.
