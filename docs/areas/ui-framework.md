# UI framework: components, forms, navigation and assets

Shared C# components, forms, list kit, in-place navigation, assets and frontend tests.
Index: [CODEMAP](../CODEMAP.md).

## Decisions

Owner decisions; change them only with the owner.

- A list the person puts in order is a [SortableList](../../src/Jularr.Frontend/Framework/Components/SortableList.cs) (`Form.Sortable`): rows with a
  drag handle (pointer events; arrow keys on the handle), the position, an icon tile, title and one state line, a switch and a menu of moves; the rows
  send their keys in order and the switched-on ones, and every move counts as a change of the save dock.
- Times use `Form.Time` ([TimePicker](../../src/Jularr.Frontend/Framework/Components/TimePicker.ts)): hour and minute fields on a desktop, a wheel
  sheet on Android, the system's own picker on iOS; the native time field stays the submitted value and the picker without script.
- `Form.File` adds a file field from a Backend `FileField` (accepted extensions, size) as the
  [FileInput](../../src/Jularr.Frontend/Framework/Components/FileInput.cs): a drop zone with a button, the chosen file with its size and a remove
  button; the native file field stays behind it (it opens the system dialog and is the submitted value, and without script it shows as a styled
  field).
  Every file field uses it (the page opts in with `FileInput.entry.ts`); never a bare native file control.
  It makes the form a multipart upload; the page handler wraps `IFormFile` into `UploadedFile` and declares its request size.
- `Form.Select` may declare a local draft refresh action and a fallback button label.
  `Form.entry.ts` replaces only the server-rendered form on a field change; the action preserves the draft and performs no mutation.
  The native button remains usable without JavaScript.
  Pages opt into the component asset.
- A choice that is its own save (`Form.Select(..., submitOnChange: true)`, such as a release group's rating) posts its form as an action on change;
  its button carries `data-form-submit-fallback` and is hidden by script.
  An optional one (nullable field) goes back to none when its chosen segment is clicked again (`data-form-clearable`).
- `npm ci` (locked package versions), `npm run check` (TypeScript, Vite, SVG sprite), `npm test`, then `dotnet build` are required in Frontend CI.
  Production publish **must not proceed** without built assets.
  Nothing compiles Sass/JS or computes hashes on a live request.
- Vite writes only minified/compiled output into `wwwroot/build/assets` and emits the manifest at `Assets/Generated/manifest.json` (private, copied to
  published host).
  `FrontendAssets` parses it once per process to render just the hashed CSS/JS for the shell and current page.
  Unused module bundles are not referenced by other pages.
- SVG source icons may live with the owning Page/Module/Component or in the shared icon library.
  The build creates one fingerprinted symbol sprite and a private symbols manifest, referenced through `FrontendAssets.IconHref`.
  No unversioned sprite URLs and no loading each small icon separately.
- **Hash by content, not release number.** Fingerprinted CSS/JS/SVG assets receive `Cache-Control: public,max-age=31536000,immutable`; unchanged
  assets keep their URLs/cache across Jularr updates.
  Nonfingerprinted public files revalidate.
  HTML, JSON, CSS, JS and SVG leave compressed (Brotli, else gzip, fastest level; `Framework/Compression.cs`); images, media and the live event stream
  do not.
  Page HTML and account/profile-specific responses use private/no-store and must not share caches across users.
  Always deploy new assets before the HTML/manifest that references them, and retain old hashes during rolling deployments until old pages have
  expired.
- Development-only design workshops may show explicitly labeled sample data and disabled component states.
  They must not start library playback or pretend to perform business actions.
  An explicitly requested promotional trailer may load its official provider embed after Play or sustained hover, without a playback session or
  progress.
  Reuse real components and approved references, not copies.

## Frontend

- [Program.cs](../../src/Jularr.Frontend/Program.cs): single host, service registration, authorization, request culture and Razor/API routing.
- [Page.cs](../../src/Jularr.Frontend/Framework/Page.cs): page definitions, shared read errors and mutation redisplay.
- [Elements.cs](../../src/Jularr.Frontend/Framework/Elements.cs), [UiComponent.cs](../../src/Jularr.Frontend/Framework/UiComponent.cs): encoded HTML
  primitives and Razor boundary.
- [Form.cs](../../src/Jularr.Frontend/Framework/Components/Form.cs), [BaseField.cs](../../src/Jularr.Frontend/Framework/BaseField.cs): canonical
  Backend metadata, invariant form binding, antiforgery and field/form errors; placeholders and the label's (i)
  ([FieldInfo.cs](../../src/Jularr.Frontend/Framework/Components/FieldInfo.cs)) come from `hint.<Fields>.<Field>` texts in
  [UiResources.cs](../../src/Jularr.Frontend/Localization/UiResources.cs).
- [FolderPicker.cs](../../src/Jularr.Frontend/Framework/Components/FolderPicker.cs),
  [client](../../src/Jularr.Frontend/Framework/Components/FolderPicker.entry.ts): native folder dialog using authorized SSR directory fragments,
  preserving form drafts and supporting cursor navigation.
  Storage and Setup share the same picker; [StorageBrowser](../../src/Jularr.Frontend/Framework/Components/StorageBrowser.cs) lists the connections
  and the `Storage__LocalPaths` not yet connected (choosing one connects it first); `FolderPickerTests` and `SmbStorageTests` cover it.
- [SavePill](../../src/Jularr.Frontend/Framework/Components/SavePill.cs), [client](../../src/Jularr.Frontend/Framework/Components/SavePill.ts): one
  save action for the page's `Form.Save` forms, rendered as the [ActionDock](../../src/Jularr.Frontend/Framework/Components/ActionDock.cs) in its
  form-page mode (`ActionDock.ForForm`: "N changes", Discard, Save); `SavePillTests` cover save, discard, a failing form and the settings controls.
- Actions (a form that posts): [ActionSwap](../../src/Jularr.Frontend/Modules/Navigation/ActionSwap.ts),
  [rules](../../src/Jularr.Frontend/Modules/Navigation/Action.ts), [tests](../../src/Jularr.Frontend/Tools/action.test.mjs) swap only the
  [Region](../../src/Jularr.Frontend/Framework/Components/Region.cs) around the form (else the page content) and keep scroll, open sections and focus;
  [ActionToast](../../src/Jularr.Frontend/Framework/Components/ActionToast.cs),
  [client](../../src/Jularr.Frontend/Framework/Components/ActionToast.ts) is the quiet hint that `JularrPage.Hint` sets
  ([Page.cs](../../src/Jularr.Frontend/Framework/Page.cs)); [Modal](../../src/Jularr.Frontend/Framework/Components/Modal.cs) is the one dialog
  structure (title row with X, body, footer with `Form.Cancel` and the primary action; `Modal.Confirm` is the short confirmation of a destructive
  action such as Disconnect: the question as the title, Cancel and a warning button); `ActionHintTests` and `WorkPageTests` cover them.
- [Range values](../../src/Jularr.Frontend/Framework/Components/Range.ts) behind `Form.Range`: native numeric sliders and their localized display (the
  number follows the slider; `Form.entry.ts` loads on every page); [tests](../../src/Jularr.Frontend/Tools/range.test.mjs),
  `FrontendAuthenticationTests.Form_Range`, `ReadingPathTests`.
- Shared field controls: [Dropdown](../../src/Jularr.Frontend/Framework/Components/Dropdown.ts)
  ([entry](../../src/Jularr.Frontend/Framework/Components/Dropdown.entry.ts), [styles](../../src/Jularr.Frontend/Framework/Components/Dropdown.scss))
  turns every native select into a button with its own popup list, the select staying hidden in the form;
  [Checkbox](../../src/Jularr.Frontend/Framework/Components/Checkbox.cs) (box, round radio, `.ui-checkbox` label);
  [NumberStepper](../../src/Jularr.Frontend/Framework/Components/NumberStepper.ts) (plus and minus of `Form.Number`); `FieldAffix` in
  [Form.cs](../../src/Jularr.Frontend/Framework/Components/Form.cs) (icon prefix, `unit.*` suffix; a `SelectOption` with an `Icon`, such as a flag,
  shows the chosen one as the prefix); [tests](../../src/Jularr.Frontend/Tools/dropdown.test.mjs),
  [tests](../../src/Jularr.Frontend/Tools/sortable-list.test.mjs).
- [SortableList](../../src/Jularr.Frontend/Framework/Components/SortableList.cs) (`Form.Sortable`),
  [client](../../src/Jularr.Frontend/Framework/Components/SortableList.entry.ts),
  [rules](../../src/Jularr.Frontend/Framework/Components/SortableList.ts): rows put in order by dragging a handle, the arrow keys or a menu, with
  switches; the Settings page's home order uses it (`HomeLayoutTests`, `SavePillTests`).
- [LocalTime](../../src/Jularr.Frontend/Framework/Components/LocalTime.cs), [styles](../../src/Jularr.Frontend/Framework/Components/LocalTime.scss),
  [client](../../src/Jularr.Frontend/Framework/Components/LocalTime.entry.ts), [date rules](../../src/Jularr.Frontend/Framework/BrowserTimeZone.cs):
  browser-local UTC instants and calendar/work civil dates; HTML and dynamically swapped fragments update in place without reloads; one midnight
  timer, no periodic polling.
  Tests: `local-time.test.mjs`, `BrowserTimeZoneTests`, `TimeZoneTests`.
- [TimePicker](../../src/Jularr.Frontend/Framework/Components/TimePicker.ts) behind `Form.Time`: desktop fields, Android wheel, iOS system picker;
  [tests](../../src/Jularr.Frontend/Tools/time-picker.test.mjs).
- [Table.cs](../../src/Jularr.Frontend/Framework/Components/Table.cs): typed rows and composed cells.
- [FileInput](../../src/Jularr.Frontend/Framework/Components/FileInput.cs),
  [client](../../src/Jularr.Frontend/Framework/Components/FileInput.entry.ts), [rules](../../src/Jularr.Frontend/Framework/Components/FileInput.ts)
  behind `Form.File`: drop zone, chosen file with size, remove, refused files with their reason;
  [tests](../../src/Jularr.Frontend/Tools/file-input.test.mjs), E2E `FileInputTests` (volume upload) and `MediaImportTests`.
  [CodeSample](../../src/Jularr.Frontend/Framework/Components/CodeSample.cs): a closed sample with a read-only box and a copy button.
  [SelectLinkMenu](../../src/Jularr.Frontend/Framework/Components/SelectLinkMenu.cs) (native details menu of links with a button showing the current
  choice, [client](../../src/Jularr.Frontend/Framework/Components/SelectLinkMenu.entry.ts)),
  [FilterDialog](../../src/Jularr.Frontend/Framework/Components/FilterDialog.cs) (side sheet around a GET form,
  [client](../../src/Jularr.Frontend/Framework/Components/FilterDialog.entry.ts)): used by the Calendar.
- [Button.cs](../../src/Jularr.Frontend/Framework/Components/Button.cs), [Tag.cs](../../src/Jularr.Frontend/Framework/Components/Tag.cs),
  [Toggle.cs](../../src/Jularr.Frontend/Framework/Components/Toggle.cs): shared controls.
- [HitArea.scss](../../src/Jularr.Frontend/Framework/Components/HitArea.scss): the phone 44px tap-target mixins (`phone-hit-area`, `phone-hit-pad`)
  used by the shared controls.
- Admin list kit (Jobs, Queue, Wanted, Works, History and Requests are built from it):
  [ListToolbar](../../src/Jularr.Frontend/Framework/Components/ListToolbar.cs) (the one row above a list: filter chips left, tools right),
  [ViewSwitch](../../src/Jularr.Frontend/Framework/Components/ViewSwitch.cs) (icon links between a list's views),
  [ActionDock](../../src/Jularr.Frontend/Framework/Components/ActionDock.cs) (the floating pill of the page: actions and bulk zones as slots on the
  shared `floating-pill` of the save pill, [client](../../src/Jularr.Frontend/Framework/Components/ActionDock.entry.ts),
  [tests](../../src/Jularr.Frontend/Tools/action-dock.test.mjs)), **Paging: set `.Paged()` on the list**
  ([Paging.cs](../../src/Jularr.Frontend/Framework/Components/Paging.cs): `Table`/`ListPanel.Paged(pageSize, key, primary)`,
  `content.Paged(rows, ...)` for anything else; next iff the page is full, no counts; the primary list's pager joins the dock by itself, others draw
  it under themselves; [tests](../../tests/Jularr.Backend.Tests/PagingTests.cs)),
  [ListPanel](../../src/Jularr.Frontend/Framework/Components/ListPanel.cs) (rows on a grid with a head, `ListColumn`/`ListRow`/`ListIdentity`; a
  container query drops columns by `HideLevel` and turns a row into a card on a phone, the page's stylesheet only sets `--list-columns`, `-medium`,
  `-narrow`), [ListFilter](../../src/Jularr.Frontend/Framework/Components/ListFilter.entry.ts) (`SearchBox` with `listFilter` narrows the rows as one
  types, [tests](../../src/Jularr.Frontend/Tools/list-filter.test.mjs)),
  [LiveSearch](../../src/Jularr.Frontend/Framework/Components/LiveSearch.entry.ts) (`SearchBox` with `live` shows the page with the query while one
  types; same-page links and GET forms take the typed words, [tests](../../src/Jularr.Frontend/Tools/live-search.test.mjs)),
  [IconTile](../../src/Jularr.Frontend/Framework/Components/IconTile.cs) (tinted icon tile, `UiTone`),
  [StateChip](../../src/Jularr.Frontend/Framework/Components/StateChip.cs) (state pill with icon),
  [KpiTiles](../../src/Jularr.Frontend/Framework/Components/KpiTile.cs) `.Strip()` (one panel of live figures, the Queue's), `NavigationTabs` modifier
  `filters` (chips with `NavigationTab.Icon` and counts), [MediaCard](../../src/Jularr.Frontend/Framework/Components/MediaCard.cs) `.Select` for a
  poster view, icons only from [Assets/Icons](../../src/Jularr.Frontend/Assets/Icons) (stroke style),
  [Drawer](../../src/Jularr.Frontend/Framework/Components/Drawer.cs) as the details of a row (`sheet`: bottom sheet on a phone; `State`, `Facts`,
  `Block`, `Setting`), `Button.Soft()`, `Tag.Warning()`, [SwitchButton](../../src/Jularr.Frontend/Framework/Components/SwitchButton.cs).
- [Split.cs](../../src/Jularr.Frontend/Framework/Components/Split.cs), [Tabs.cs](../../src/Jularr.Frontend/Framework/Components/Tabs.cs),
  [NavigationTabs.cs](../../src/Jularr.Frontend/Framework/Components/NavigationTabs.cs): layout and route-backed navigation.
- [Shared layout](../../src/Jularr.Frontend/Pages/Shared/_Layout.cshtml), [page body](../../src/Jularr.Frontend/Pages/Shared/_PageBody.cshtml): thin
  Razor bridges.
- [FrontendAssets.cs](../../src/Jularr.Frontend/Framework/FrontendAssets.cs), [Vite](../../src/Jularr.Frontend/vite.config.ts), [build
  tools](../../src/Jularr.Frontend/Tools): fingerprinted, entry-scoped assets and SVG sprite.
- [Shared SCSS](../../src/Jularr.Frontend/Assets/Shared/shell.entry.scss): colocated base component styles loaded once.
- [In-place navigation](../../src/Jularr.Frontend/Modules/Navigation/PageSwap.ts),
  [rules](../../src/Jularr.Frontend/Modules/Navigation/Navigation.ts), [tests](../../src/Jularr.Frontend/Tools/navigation.test.mjs): htmx boost swaps
  `#app-content`; an expired 401 navigates to sign-in with a local return URL (POST actions are never replayed); `onPage` page-script setup,
  `inserted` for script HTML, the player keeps its own address while it plays.
- [ByteSize.cs](../../src/Jularr.Frontend/Framework/ByteSize.cs): file and volume sizes in decimal units for every page.
- [FactList](../../src/Jularr.Frontend/Framework/Components/FactList.cs): labelled numbers and states (statistics, system overview).
- [Sparkline](../../src/Jularr.Frontend/Framework/Components/Sparkline.cs): a server-drawn line over evenly spaced values (system performance).
- [ResourceChart](../../src/Jularr.Frontend/Framework/Components/ResourceChart.cs) (processor and memory of 24 hours, drawn by the server),
  [ShareBar](../../src/Jularr.Frontend/Framework/Components/ShareBar.cs) (one SVG bar that shows how a whole divides, with its legend); `KpiTile`
  takes a foot line for the Dashboard's cards, `UiSection` takes an action at the heading's end and `Table.Dense()` is the data table of figures
  inside a card.
- User catalog/inbox lists reuse `InfiniteList`: Favorites, collection contents, a person's Works, franchise contents, requests and notifications load
  48 entries at a time.
  Stable `data-list-key` values prevent duplicate entries; partial component setup keeps appended live progress and truncated text current.
  E2E: `UserListScrollTests` (all six lists, request details/Back, appended favorite membership and native paging).
- [InfiniteList.ts](../../src/Jularr.Frontend/Framework/Components/InfiniteList.ts) owns the HTML loader; the app entry adds page lifecycle/history
  handling.
  [Reader Notes](../../src/Jularr.Frontend/Modules/Reader/Notes.ts) uses it inside the scrolling notes panel with the authorized
  `Reader.OnGetNotesAsync` fragment, which reads source visibility and 48 notes without opening the book.
  Details file tabs retain grouped tables and actions while appending 48 inventory entries.
  Native links remain available; `UserPanelScrollTests` and `ReadingTests.ReaderNotes_Fragment*` verify browser behavior and access.
- [Truncated](../../src/Jularr.Frontend/Framework/Components/Truncated.cs), [client](../../src/Jularr.Frontend/Framework/Components/Truncated.ts): one
  line of data text with an ellipsis that shows its full text on hover, focus and touch only while it is cut off; pages opt in with
  `Truncated.entry.ts` ([tests](../../src/Jularr.Frontend/Tools/truncated.test.mjs)).

## Tests

- [Asset tests](../../src/Jularr.Frontend/Tools/asset-manifest.test.mjs), [appearance tests](../../src/Jularr.Frontend/Tools/appearance.test.mjs),
  [structure tests](../../src/Jularr.Frontend/Tools/shell-login.test.mjs).
- [Discover tests](../../src/Jularr.Frontend/Tools/discover.test.mjs), [progress tests](../../src/Jularr.Frontend/Tools/progress.test.mjs),
  [Reader/Player tests](../../src/Jularr.Frontend/Tools/reader.test.mjs), [translation tests](../../src/Jularr.Frontend/Tools/translation.test.mjs),
  [comic tests](../../src/Jularr.Frontend/Tools/comic.test.mjs), [navigation tests](../../src/Jularr.Frontend/Tools/navigation.test.mjs), [UI-key
  verification](../../src/Jularr.Frontend/Tools/verify-ui-resources.mjs).
- Backend tests: FrontendAuthenticationTests, DiscoverTests, MediaTests, LibraryTests, ReadingTests and PlaybackTests; isolated PostgreSQL only.
- [Design lab](../../src/Jularr.Frontend/Pages/Development/Design/Design.cshtml.cs),
  [ShellPreview](../../src/Jularr.Frontend/Pages/Development/ShellPreview/ShellPreview.cshtml.cs): Development-only; browser verification is separate.
- Remaining: persistent shell (it is part of the swapped content), previous/next playback targets, provider facets, complete episode details, profile
  media visibility/order, metadata-locale policy, Setup backup/restore, PIN/quota/rules.
