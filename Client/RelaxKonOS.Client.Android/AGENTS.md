# Android documentation policy

`docs/` in this project is the canonical home for Android product, design,
support-scope, and release documentation. Keep mobile-only decisions
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

Classify Android documents under `docs/design/` (current rules and support
boundaries), `docs/features/` (implemented workflows), and `docs/development/`
(build, release, and verification requirements). Keep `docs/README.md` as the
index. Describe current behavior directly; do not maintain goal tables,
rollout stages, implementation progress, or historical test execution diaries.
Preserve unsupported capabilities as explicit current boundaries, not future
commitments. Verification requirements do not imply tests have passed.
Git retains history; do not add archive copies or redirects at retired paths.
