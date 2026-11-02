# Profile scoring and release groups

Mockup: [index.html](index.html) (three views). Issue: #1711, parents #865 and #1706. Status: owner decisions recorded 2026-10-29 and approved; built: release groups and search results (views 2 and 3, #1725) and the profile page (view 1).

## Decisions (owner, 2026-10-29)

1. One scoring system for every media type: rule effects Require / Prefer / Avoid / Reject, one score. The media type only changes which conditions exist.
2. Timed fallback tiers and "Temporary accepted" of #865 are dropped. The best acceptable release is grabbed at once; upgrades improve later. No wait times anywhere.
3. Release group ratings: **Good** (plus points), **Bad** (minus points), **Blocked** (always reject). Default +50 / -50, editable per profile.
4. The group list is per media type and collects every group seen in a search result (last seen, hit count), so a group can be rated before anything is downloaded. A group can also be typed by hand.
5. Release groups have their own tab in Profile (one tab row, media-type switch as today). The rating applies to every profile of that media type.
6. Simple mode is the default profile editor: format ranking, size per volume, language order, release group points, upgrade target, minimum score. Custom formats with points stay behind "Erweitert".
7. Upgrades stop when the target quality is reached **and** the score is at least the target score; a candidate must also beat the existing file by a minimum score gain.
8. A rating is reset to not rated (neutral) by clicking the already selected segment again (also Space or Enter on it); no extra control. Several groups in one release name (separated by comma, ampersand, plus or slash) are separate groups; publishers and imprints are never groups.
9. Book rules from #1706 stay: EPUB > AZW3/MOBI > PDF, illustrated higher; size limits per volume by kind; packs allowed when they cover the wanted volumes; audiobooks rejected for e-book requests.

## Views

The mockup is built from the real Jularr page parts (shell, tabs, form fields, list, table), not drawn anew. Three views, switch at the bottom left.

1. **Profile (Simple):** the existing profile page with sections General, Qualities (ranking, stop upgrading at), Size, Languages, **Release groups** (new: score for a good and a bad group, link to the group list), **Upgrades** (the existing minimum format score, upgrade-until score and minimum score increase, moved up from Format scores) and a collapsed **Advanced** with Quality groups and the per-format scores. The two large "Move up" / "Move down" buttons per quality row become two compact round arrow buttons (first row cannot move up, last row cannot move down; same accessible names). The save pill works as on every profile page.
2. **Release groups (new tab after Release profiles):** media-type switch as on the other tabs, KPI strip (groups, good, not rated, blocked), a field to find or add a group, and a list: group name with a sub line (seen N times, last seen), one segmented control per group (the real Jularr segment control: Good | Bad | Blocked, none selected means not rated). Not rated groups are listed first. A rating saves on click and applies to every profile of the media type.
3. **Search results:** the existing table gets a **Group** column; the score keeps its hover (title) listing every rule with points, now including the group rule; the reason names the rating (Group rated good, Group blocked). Blocked groups are rejected with that reason.

## Rules

- Evaluator: the existing `AcquisitionSelectionLogic` and judges; the group rating becomes one more named rule with points, shown in the sum. No second engine.
- A Blocked group rejects in automatic search; in manual search the row is marked and can still be selected.
- Group names are normalised (case-insensitive, trimmed) and stored once per media type.
- The group is read from the release name by the parser of #1706 and the video parsers; a release without a group is unaffected.
- Tiebreak order of #865 stays; group rating is a rule, not a tiebreak.

## Open before building

- Table design: one `ReleaseGroups` table (media type, normalised name, rating, last seen, hits) or ratings inside the profile definition. Owner decision is "per media type", which favours one table.
- Existing `FormatConditionKind.ReleaseGroup` conditions in custom formats keep working next to the rating; whether the Release-Gruppen tab imports them is open.
- Migration number: 0028_release_groups (0026 downloads page, 0027 smart pages view).
- Recyclarr/TRaSH templates (#1190) and a profile simulation are out of scope here.
