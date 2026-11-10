# Instance settings, modules, providers and setup

Instance settings, module switches, media visibility, the provider registry and the first-run setup.
Index: [CODEMAP](../CODEMAP.md).
Specs: [INSTANCE_SETUP](../INSTANCE_SETUP.md).

## Decisions

Owner decisions; change them only with the owner.

- A flow with steps (the setup) shows them in one [Stepper](../../src/Jularr.Frontend/Framework/Components/Stepper.cs) at its top, with no heading,
  subtitle or help line of its own per step.
  The setup stands alone (no application sidebar, header or tab row; every topic is its own step, the last one the overview with Finish); adding a
  **new** connection, indexer or client is a dialog with Cancel, configuring an **existing** provider or service is inputs inline in its card.
- The instance settings are five pages below `/Admin/Instance` (Instance, Modules, Media types, Languages, Naming) with one chip row (the Settings
  entry of [AdminMenu](../../src/Jularr.Frontend/Framework/AdminMenu.cs)), sections inside a page instead of more tabs, one save pill for all of it.
  A switch that is a setting is a form field (`Form.Switch`, shown as a [SettingCard](../../src/Jularr.Frontend/Framework/Components/SettingCard.cs)),
  so toggling reloads nothing and saves with the pill; settings of a module appear only while it is on; a module that needs another shows it and
  cannot be switched on while that is off, and one that others need names them and stays on.

## Backend

- [ProviderServices.cs](../../src/Jularr.Backend/Service/Admin/Provider/V1/ProviderServices.cs),
  [Providers.cs](../../src/Jularr.Backend/Data/Admin/Provider/V1/Providers.cs): existing provider registry enablement, not connection/capability
  management.
- [InstanceSettingsServices.cs](../../src/Jularr.Backend/Service/Admin/Instance/V1/InstanceSettingsServices.cs),
  [InstanceSettings.cs](../../src/Jularr.Backend/Data/Admin/Instance/V1/InstanceSettings.cs): Owner name/language reads and version-checked edits;
  [InstanceLogic.cs](../../src/Jularr.Backend/Logic/Instance/InstanceLogic.cs) owns mutations and enabled-locale locking.
  [Instance.sql](../../database/Instance.sql) is a fresh singleton baseline, not a migration.
- [InstanceModuleServices.cs](../../src/Jularr.Backend/Service/Admin/Instance/V1/InstanceModuleServices.cs),
  [InstanceModuleLogic.cs](../../src/Jularr.Backend/Logic/Instance/InstanceModuleLogic.cs): Owner module editing (one switch, or every switch in one
  step) and configuration locks; `InstanceModuleDependencies` (database/Instance.sql) names the modules a module needs, and a save that leaves an
  enabled module without one is refused (`module_dependency`).
  [ReadInstanceCapabilitiesV1.cs](../../src/Jularr.Backend/Service/User/Instance/V1/ReadInstanceCapabilitiesV1.cs) returns the caller's verified
  grants and the usable enabled modules; the Frontend checks them with `UiText.Can` and names page offers once in
  [Abilities.cs](../../src/Jularr.Frontend/Framework/Abilities.cs);
  [ReadInstancePresentationV1.cs](../../src/Jularr.Backend/Service/Public/Instance/V1/ReadInstancePresentationV1.cs) exposes only name/default
  language.
- Media visibility: views in [Media.sql](../../database/Media.sql); switches in
  [InstanceMediaServices.cs](../../src/Jularr.Backend/Service/Admin/Instance/V1/InstanceMediaServices.cs), Owner blocks in
  [ProfileMediaAccessServices.cs](../../src/Jularr.Backend/Service/Admin/Account/V1/ProfileMediaAccessServices.cs), personal hiding in
  [ProfileMediaServices.cs](../../src/Jularr.Backend/Service/User/Profile/V1/ProfileMediaServices.cs); writes in
  [MediaCategoryLogic.cs](../../src/Jularr.Backend/Logic/Media/MediaCategoryLogic.cs).
- [SetupServices.cs](../../src/Jularr.Backend/Service/Public/Setup/V1/SetupServices.cs),
  [Setup.cs](../../src/Jularr.Backend/Data/Public/Setup/V1/Setup.cs): Public first-Owner availability and creation, field/password confirmation
  validation; Account Logic owns permanent bootstrap closure and Session Logic signs in atomically.
- [ProviderLogic.cs](../../src/Jularr.Backend/Logic/Provider/ProviderLogic.cs): enabled-provider lookup and configuration lock;
  [ProviderData.cs](../../src/Jularr.Backend/Data/Logic/Provider/ProviderData.cs): typed neutral lookup data.
- [InstanceSetupStateFields.cs](../../src/Jularr.Backend/Data/Fields/Setup/InstanceSetupStateFields.cs): canonical singleton bootstrap fields; Account
  Logic owns their transactional closure.
  Wizard step and start (the steps in flow order), checks and completion (films need TMDB's key, series TheTVDB's):
  [SetupProgressServices.cs](../../src/Jularr.Backend/Service/Admin/Instance/V1/SetupProgressServices.cs) and
  [SetupLogic.cs](../../src/Jularr.Backend/Logic/Instance/SetupLogic.cs).

## Frontend

- [Stepper](../../src/Jularr.Frontend/Framework/Components/Stepper.cs) (the steps of a flow in one row,
  [styles](../../src/Jularr.Frontend/Framework/Components/Stepper.scss)) and
  [ModuleCard](../../src/Jularr.Frontend/Framework/Components/ModuleCard.cs) (a module's card with its needs and the fields that belong to it, used by
  the Modules page; the Caching module's days and sub-options come from
  [CachingModule](../../src/Jularr.Frontend/Framework/Components/CachingModule.cs), also on the setup's module step).
- Setup (a standalone flow, no application menus; [Setup contract](../INSTANCE_SETUP.md)):
  [SetupWizard.cs](../../src/Jularr.Frontend/Framework/SetupWizard.cs) owns the steps in their order, the
  [Stepper](../../src/Jularr.Frontend/Framework/Components/Stepper.cs) at the top, Back and Next and the resume mapping and frames every page under
  `/Setup` (set in [Page.cs](../../src/Jularr.Frontend/Framework/Page.cs)); [SetupGate.cs](../../src/Jularr.Frontend/Framework/SetupGate.cs) leads
  every page to the saved step until Finish (the Owner continues, others see
  [Waiting](../../src/Jularr.Frontend/Pages/Setup/Waiting/Waiting.cshtml.cs));
  [Progress](../../src/Jularr.Frontend/Pages/Setup/Progress/Progress.cshtml.cs) resumes and moves on from a step without a form.
  Steps: [Owner](../../src/Jularr.Frontend/Pages/Setup/Start/Start.cshtml.cs) (user name, profile name, passwords with the
  [eye](../../src/Jularr.Frontend/Framework/Components/PasswordReveal.cs), language with its flag),
  [Begin](../../src/Jularr.Frontend/Pages/Setup/Begin/Begin.cshtml.cs) (new, takeover, restore),
  [Storage](../../src/Jularr.Frontend/Pages/Setup/Storage/Storage.cshtml.cs) (the Admin connection cards: offered local paths as tiles, add dialogs),
  [Takeover](../../src/Jularr.Frontend/Pages/Setup/Takeover/Takeover.cshtml.cs) (a card per system, preview and takeover on the Admin takeover form
  parts), [Modules](../../src/Jularr.Frontend/Pages/Setup/Instance/Instance.cshtml.cs) (sets, basic functions, media types with their modules in
  [OptionList](../../src/Jularr.Frontend/Framework/Components/OptionList.cs)), quality profiles,
  [Libraries](../../src/Jularr.Frontend/Pages/Setup/Libraries/Libraries.cshtml.cs),
  [Requests](../../src/Jularr.Frontend/Pages/Setup/Requests/Requests.cshtml.cs), download storage, download clients and indexers (the Admin pages with
  cards, [Acquisition](../../src/Jularr.Frontend/Pages/Admin/Acquisition/Acquisition.cshtml.cs)),
  [Providers](../../src/Jularr.Frontend/Pages/Setup/Providers/Providers.cshtml.cs) and
  [Overview](../../src/Jularr.Frontend/Pages/Setup/Overview/Overview.cshtml.cs).
  [StorageBrowser](../../src/Jularr.Frontend/Framework/Components/StorageBrowser.cs) serves the picker of connections and folders.
- Settings, one chip row ([AdminMenu](../../src/Jularr.Frontend/Framework/AdminMenu.cs), as every entry's pages) over five pages below
  `/Admin/Instance`: [Instance](../../src/Jularr.Frontend/Pages/Admin/Instance/Instance.cshtml.cs) (sections of one form with sliders, module-only
  settings appear only with their module), [Modules](../../src/Jularr.Frontend/Pages/Admin/Modules/Modules.cshtml.cs) (`/Modules`:
  [SettingCard](../../src/Jularr.Frontend/Framework/Components/SettingCard.cs) switches in one form saved atomically; the Backend refuses a module
  whose needs are off or that others need, and [Modules.ts](../../src/Jularr.Frontend/Pages/Admin/Modules/Modules.ts) shows the same rules while
  switching; `modules.test.mjs`), [Media types](../../src/Jularr.Frontend/Pages/Admin/MediaTypes/MediaTypes.cshtml.cs) (`/MediaTypes`),
  [Languages](../../src/Jularr.Frontend/Pages/Admin/Languages/Languages.cshtml.cs) (`/Languages`, a card per language with its name, state and
  direction as inputs saved by the pill; adding is a dialog with Cancel) and [Naming](../../src/Jularr.Frontend/Pages/Admin/Naming/Naming.cshtml.cs)
  (`/Naming`: patterns for every media type, narrowed by the shared media-type bar (`?type=`), with catalog IDs, separate season/daily formats and
  live single/multi/daily/special examples from `ReadNamingPreviewV1`, a drawer of placeholders that fill the last pattern or Specials folder field;
  the Specials folder uses the same tokens and character rules as regular names;
  [Naming.entry.ts](../../src/Jularr.Frontend/Pages/Admin/Naming/Naming.entry.ts)); tests `InstanceModuleDependencyTests`, `NamingPatternTests`, E2E
  `SettingsPagesTests`, `SavePillTests`.
- [Providers](../../src/Jularr.Frontend/Pages/Admin/Providers/Providers.cshtml.cs): the registry's providers as cards (state, whether a key is stored,
  switch through the card's region, Test for connected servers; the protected key, the Plex server or the Jellyfin/Emby connection are inputs in the
  card, saved by the page's pill; Sync and Disconnect for a connected server); `?view=servers` shows the media servers with the list of their users
  and the profile each shares progress with; the Integrations entry's chip row (Providers, Media servers once one is set up, Discover catalog, with
  the counts the page gives `AdminMenu.Row`).
  `StorageIntegrationsTests`.

## Tests

- First Owner, language enablement/defaults, bootstrap closure/concurrency, module-step atomicity, empty-library gating, source saves and completion:
  `SetupTests.cs`; the first choice and the steps in their order: `SetupTests.cs`; browser flow: E2E `SetupFlowTests`.
  Setup orchestration: [SetupModuleServices.cs](../../src/Jularr.Backend/Service/Admin/Instance/V1/SetupModuleServices.cs),
  [SetupProviderServices.cs](../../src/Jularr.Backend/Service/Admin/Instance/V1/SetupProviderServices.cs),
  [SetupProgressServices.cs](../../src/Jularr.Backend/Service/Admin/Instance/V1/SetupProgressServices.cs).
- Instance settings, Owner-only access, enabled locales, stale edits and real Razor forms: `InstanceSettingsTests.cs`.
- Real module gating, data retention, All/Any/write locks, public branding/default locale and capability-driven Razor navigation:
  `InstanceCapabilitiesTests.cs`.
