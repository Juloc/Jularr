# Admin Users & Permissions — V1

Status: **binding V1 planning specification; approved mockup direction.**

Text specification wins over the visual reference on conflict.

## Purpose
Manage accounts/profiles, groups/roles and effective capabilities that derive both API authorization and visible app shell.

## Page structure
User list -> user detail -> groups/roles -> capability matrix -> media/request/learning/AI policies -> login identities/profile policy -> devices/sessions.

## Data / information
Internal Account/Profile IDs, linked Login identities, Profile ownership, groups/roles, effective capabilities, media-type visibility, Request approval rights, profile restrictions and active sessions.

Where the existing capability model still uses `Request` vs `Instant`, both map to the same consumer `Request` action. `Instant` means the request may be auto-approved immediately; it does not expose a separate Add/Instant button.

Account and Profile are distinct:
- Account owns authentication/security/roles.
- Profile owns personal media state/preferences.
- External provider IDs never replace the internal Account/Profile IDs.

## Actions
Create/invite where supported, enable/disable, assign groups/roles, edit capabilities, manage allowed Profiles/profile limits, inspect/reset Profile PIN where authorized, inspect/revoke linked Login identities safely, revoke sessions, inspect effective permission explanation.

Instance-wide Login-provider enablement/configuration belongs to provider/auth settings; this page manages which identities belong to a specific Account and the resulting user/profile policy.

## AI authorization boundary

Users & Permissions owns the canonical Account/Profile/group/role capability that determines whether a subject is eligible to use shared instance AI.

Admin AI owns AI-service-specific narrowing and resource policy such as:
- allowed AI feature categories;
- request/token/image limits;
- concurrency/queue policy;
- budgets/cost ceilings where supported.

Effective access is the intersection of both contracts. Neither page may persist a separate AI identity/group/role model.

## Light / Dark
Both first-class Admin surfaces.

## Platforms
Desktop primary; tablet/mobile stacked user detail and grouped permission editors. TV unsupported.

## States
No users, pending/inactive, permission inherited/overridden, conflicting/invalid policy, active sessions, forbidden action, error.

## Must not implement
No scattered hard-coded `IsAdmin` assumptions, no UI-only authorization, no inferred permissions from hidden navigation, no exposure of another user's personal secrets, no separate AI permission model disconnected from Accounts capabilities.