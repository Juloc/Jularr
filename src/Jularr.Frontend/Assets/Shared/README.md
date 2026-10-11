# Shared shell assets

This is the intentionally global shell layer only. Page and module-specific
styles and behavior belong beside the relevant owner. The Vite entry files
are discovered by suffix `.entry.ts` and `.entry.scss`, not a central page list.
The Vite manifest is consumed server-side to render only requested hashes.
