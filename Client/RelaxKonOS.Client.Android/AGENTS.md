# Android documentation policy

`docs/` in this project is the canonical home for Android product, design,
implementation-progress, and release documentation. Keep mobile-only decisions
here: the application catalog, phone/tablet adaptive UI rules,
internationalization, themes, Android platform boundaries, and validation
matrices.

The repository-level `docs/mobile/` folder contains only an introduction and
links. When an Android decision changes, update the relevant document here and
only update that introduction when its navigation needs to change. Do not add
duplicate detailed mobile documents at repository level.

Before changing an Android wire contract, coordinate the corresponding shared
Protocol, server, desktop, test, and documentation changes. Follow the
repository API evolution policy: make the current contract authoritative and do
not add compatibility shims.

# Documentation lifecycle

Classify Android documents under `docs/design/` (current invariants),
`docs/features/` (implemented workflows), `docs/plans/` (remaining implementation),
`docs/development/` (build and release), and `docs/status/` (current evidence and
unclosed verification). Keep `docs/README.md` as the index.

When a goal is implemented, remove its completed rollout plan and preserve any
current behavior in the feature documentation. Move outstanding device or host
checks to `docs/status/Verification.md`; missing verification alone is not a new
implementation goal. Summarize current facts in `docs/status/Progress.md` instead
of accumulating repair histories or obsolete build environments. Git retains
history; do not add archive copies or redirects at deleted document paths.
