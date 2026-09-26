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
