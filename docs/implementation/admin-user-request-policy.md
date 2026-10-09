# Admin User Request Policy — implementation slice

Status: in progress

## Canonical sources

- Users & Permissions spec: `docs/mockups/admin-users-permissions/SPEC.md`
- Per-media rights: `MediaCapabilityStore` / `MediaCapabilityPolicy`
- Request auto-approval: `AcquisitionRequestSettingsStore`
- Instance availability: `IInstanceModuleService`
- Request queue/rule editor: `/Admin/Requests`

## This slice

Add an explainable **Request access** section to the single-user Admin page without creating another
policy store.

For each enabled acquisition media kind the page shows the account's effective media capability.
That value continues to come from the canonical capability matrix. It also shows auto-approval rules
that can apply to the account, including global rules and rules targeted to that account, and links
to the canonical Requests rule editor.

The section is hidden when Acquisition is disabled instance-wide. Disabled media modules are omitted.

## Resolution

```text
instance Acquisition/media module
-> media capability (role default + optional user override)
-> request auto-approval rule, only when the capability creates a request
```

`Instant` still means the account adds immediately; an auto-approval rule is not a replacement for
that capability.

## Out of scope

- a second request policy store;
- editing global auto-approval rules inline on the user page;
- custom groups;
- Learning/AI permission policy, which needs its own canonical policy contracts.
