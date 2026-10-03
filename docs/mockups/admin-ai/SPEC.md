# Admin AI — V1

Status: approved planning direction; current AI mockup is the visual baseline once uploaded to this folder.

Global UX rules: `docs/UX.md`.

If an image and this specification conflict, this specification wins.

## Purpose

Admin AI is the instance-wide control center for Jularr's server-side AI execution.

It owns:
- server AI providers
- available models
- task routing
- fallback rules
- global feature access
- user/group AI policy
- limits/concurrency/budget controls
- persistent/generated-content policy
- AI job monitoring and usage statistics

It does not own:
- personal user API keys or personal AI settings
- feature/domain logic
- canonical media identity
- translation/learning/media business rules themselves
- global user authorization outside AI-specific capability/policy

Feature modules define task intent. AI executes the task through configured providers/models.

## Main navigation

Admin AI uses six primary sections:

1. **Übersicht**
2. **Provider & Modelle**
3. **Task Routing**
4. **Zugriff & Policies**
5. **Generierte Inhalte**
6. **Jobs & Nutzung**

Do not split Text AI, Image AI and Embedding AI into unrelated admin areas if the same provider/model system can represent their capabilities.

---

## 1. Übersicht

### Purpose

Operational summary of instance-wide AI health and activity.

### Summary cards

Show compactly:
- providers online / total
- active models
- running jobs
- errors in selected period
- optional budget/quota warning

### Period selector

Recommended:
- 1h
- 24h
- 7d
- 30d

### Usage graph

Show AI usage over time.

Switchable metrics may include:
- tokens
- images
- requests
- cost where known

Do not invent cost data when the provider does not expose enough information.

### Usage by task

Show relative usage by task category such as:
- translation
- summarization
- metadata analysis
- image generation
- learning
- OCR / extraction
- tagging / keywords
- other

### Running AI jobs

Compact table:
- task
- target/title
- provider/model
- progress
- duration
- status
- action/details

Overview is not the full job history; deep diagnostics belong to Jobs & Usage.

---

## 2. Provider & Modelle

### Purpose

Configure server-side AI providers and discover/manage the models exposed by them.

### Provider list

Default columns:
- Name
- Type
- Endpoint
- Health
- Model count
- Priority
- Actions

Provider examples may include:
- OpenAI-compatible
- Anthropic
- Gemini
- Ollama/local
- other supported adapters

The UI must be adapter-driven, not hard-coded around one vendor.

### Provider configuration

Common fields:
- name
- enabled
- endpoint/base URL
- API key/secret
- timeout
- priority
- optional organization/project
- provider-specific validated options

Secrets are write-only/masked after save.

Actions:
- test connection
- refresh/discover models
- enable/disable
- edit
- remove where allowed

### Model table

For the selected provider show:
- model name
- provider-native model ID
- version/alias where relevant
- capability type
- context size where known
- supported input types
- supported output types
- health/availability
- enabled
- actions

Capabilities may include:
- Text
- Vision
- Image generation
- Embeddings
- OCR/image-to-text
- audio where later supported
- structured output/tool use where adapter reports it

### Model override

Per model, allow optional admin overrides only where useful:
- enabled
- display name
- context/token limit override
- concurrency cap
- reasoning level/default
- temperature/default
- image size/quality defaults
- cost metadata override only when explicitly admin-supplied

Provider-native capability discovery remains the source of truth where available.

Do not silently claim unsupported capabilities.

---

## 3. Task Routing

### Purpose

Define which model Jularr uses for each stable AI task contract.

### Task table

One row per AI task.

Recommended columns:
- Task
- Description
- Primary model
- Fallback model
- Task parameters
- Enabled
- Actions

Examples:
- Translation
- Summarization
- Metadata analysis
- Image generation
- OCR / Text extraction
- Tagging / Keywords
- Content analysis
- Learning assistance
- other registered task contracts

### Primary + fallback

Each task can select:
- primary provider/model
- optional fallback provider/model
- fallback conditions

Fallback conditions may include:
- provider unavailable
- model unavailable
- rate limited
- quota exhausted
- timeout
- retryable provider failure

Do not silently fall back from a user's personal provider to the shared server provider.

### Task parameters

Task-specific safe defaults may include:
- temperature
- max output tokens
- reasoning effort
- image quality/size
- structured output requirement
- timeout
- retry count
- concurrency

Only show parameters supported by that task/model combination.

### Standard models

An optional Standardmodelle tab may define defaults by capability:
- default text model
- default vision model
- default image model
- default embedding model

Task-specific routing overrides these defaults.

---

## 4. Zugriff & Policies

### Purpose

Control who may use instance AI and which AI capabilities are available.

### Global AI switches

These are AI-service policy settings, not a substitute for the canonical Instance module system. Do not expose an AI InstanceModule toggle unless a complete runtime gate actually exists.

Possible instance settings:
- AI globally enabled
- admin-only mode
- shared server AI available to users
- local models preferred where configured
- external/cloud providers allowed
- feature categories enabled/disabled

### Feature categories

Examples:
- text generation
- translation
- image generation
- embeddings
- OCR / image-to-text
- metadata analysis
- tagging/keywords
- summarization
- learning-related AI

Feature list must come from registered capabilities/task contracts rather than scattered page-specific flags.

### Users & groups

Subjects, groups, roles and base authorization come from `admin-users-permissions`; Admin AI must not create a second account/group/role store.

The effective rule is:

`Account/Profile capability from Users & Permissions ∩ AI service policy/limits`

Admin AI may expose a focused projection of the same subjects for AI-specific restrictions/limits:

- subject type;
- user/group;
- effective AI eligibility;
- allowed AI feature categories;
- AI-specific limits/budget;
- status/explanation.

Base grants such as whether a role/account may use shared instance AI are owned by Users & Permissions. Admin AI may only narrow or parameterize that access through its AI-specific policy contract. Any inline editing must write the same canonical authorization/policy records rather than a disconnected permission model.

General account/role membership management stays in `admin-users-permissions`.

### Limits

Possible limits:
- requests/day
- tokens/day
- images/day
- concurrent jobs
- per-user/group budget where cost metadata is available
- max task duration
- queue priority

Unlimited is an explicit value.

### Budget & cost

Where provider pricing/cost data is known:
- optional monthly/day budget
- warning threshold
- hard stop threshold
- usage by provider/model/task/user

If cost cannot be determined reliably, show usage metrics only.

---

## 5. Generierte Inhalte

### Purpose

Define how persistent AI-generated artifacts are stored, reviewed, shared and replaced.

### Storage policy

Generated-content storage may distinguish:
- shared instance artifacts
- private/user-specific artifacts

Path ownership remains in Storage infrastructure. AI references configured storage roles/locations where needed rather than inventing arbitrary filesystem paths.

### Provenance

Persistent generated content stores provenance such as:
- provider
- model
- task
- parameters
- generated timestamp
- initiating actor
- source input reference where safe
- checksum/version

Prompts/input text may be retained only according to explicit privacy/retention policy.

### Publish / visibility rules

Per artifact/task category, support policies such as:
- private by default
- shared by default
- admin review required
- creator review required
- automatic publication allowed
- never auto-publish

Examples:
- generated chapter images
- translated text
- summaries
- AI-enhanced metadata
- generated artwork

### Replacement/versioning

Rules may include:
- keep original
- keep previous generated version
- replace automatically only when policy allows
- require review before replacement
- maintain provenance/version history

Never silently overwrite canonical/original content.

### Tags

Generated artifacts may receive technical provenance tags such as:
- AI-generated
- task type
- model/provider

Tags should be informational and not visually dominant in user UI.

---

## Operational-record boundary

**Jobs & Nutzung** is a domain-focused projection of AI execution plus AI-specific usage/cost telemetry. It must not create a second global Activity/History job store.

Canonical operational state remains shared with Activity / To-Do / History; AI may keep additional AI-specific usage/provenance records where required.

## 6. Jobs & Nutzung

### Purpose

Full operational history and diagnostics for AI execution.

### Job list

Default columns:
- Time
- Task
- Target/title
- Provider / Model
- Tokens / images / request size
- Duration
- Status
- User/actor
- Actions

Filters:
- task
- provider
- model
- status
- user
- date range

Tabs/subviews may include:
- Job list
- Statistics
- Errors
- Cost

### Job detail

Show:
- task contract
- target/context reference
- provider/model
- timestamps
- queue/run duration
- parameters
- token/image usage
- cost where known
- retries/fallbacks
- error classification
- correlation/job IDs
- provenance/artifact output

Do not expose secrets or sensitive provider payloads.

### Actions

Where supported:
- cancel running job
- retry failed job
- inspect output
- open related artifact
- open related Activity/History event
- export usage summary

Retry must preserve explicit task intent rather than resubmitting arbitrary raw provider requests.

### Statistics

Useful views:
- requests by task
- tokens/images by task
- usage by provider/model
- success/failure rate
- latency
- rate-limit events
- fallback rate
- concurrency utilization
- cost where known

---

## Personal AI boundary

Personal AI configuration belongs in User Settings.

Personal AI may include:
- user's own provider/API key
- preferred personal model
- private usage limits/settings

Server AI and personal AI are separate authorities.

Rules:
- no silent personal -> server fallback
- no silent server -> personal fallback
- UI must show which authority/provider is being used when it matters
- server credentials are never exposed to users
- personal credentials are never exposed to admins as plaintext

## Health model

Shared states:
- Healthy
- Degraded
- Unavailable
- Rate limited
- Quota exhausted
- Disabled
- Unknown

Model-level health may differ from provider-level health.

Partial capability failure should not mark the entire provider offline when other models/capabilities remain usable.

## Light / Dark

Both first-class.

Admin visual language:
- compact
- operational
- restrained semantic colors
- no decorative artwork
- policy/status must not depend on color alone
- dense tables remain readable

## Platforms

### Desktop

Primary platform for all six sections.

### Tablet

Supported with stacked panels, scrollable tabs and adapted tables.

### Mobile

Support operational checks and simple edits:
- overview
- provider health
- job status
- policy toggles

Complex routing/policy matrices may use full-screen editors.

### TV

Unsupported.

## Loading / Empty / Error / Partial states

Required:
- no provider configured
- provider configured but no models
- model discovery loading
- model discovery failed
- provider healthy/degraded/offline
- rate limited
- quota exhausted
- task has no eligible model
- invalid fallback
- feature disabled
- policy conflict
- job queued
- job running
- job failed
- job cancelled
- persistent artifact pending review
- budget warning
- usage unavailable
- permission denied

## Domain / architecture constraints

- AI owns execution/configuration, not feature/domain intent.
- Feature modules define stable AI task contracts.
- AI providers/models are adapters, not canonical media identity.
- Persistent generated artifacts keep provenance.
- User/group AI access integrates with Accounts capabilities/policy.
- Provider adapters never leak directly into Razor/UI domain behavior.
- Task routing must be capability-aware.
- No task may select a model lacking required capabilities.
- Jobs are operational records and may reference domain targets without owning them.

## Must not implement

- No media identity ownership in AI.
- No AI-specific duplicate media model.
- No personal/server fallback without explicit policy.
- No silent publication of generated shared artifacts.
- No silent replacement of original/canonical content.
- No cleartext provider secrets.
- No personal user secrets exposed to admins.
- No feature-specific duplicate permission systems.
- No arbitrary provider-native requests from UI.
- No model selection based solely on display name.
- No fabricated cost/usage values.
- No unrestricted generated-content filesystem paths.
