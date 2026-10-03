# Fluent System Icons

Official Microsoft Fluent System Icons, Regular 24px SVG snapshot retrieved on 2026-10-03.
The unmodified originals, source URLs and MIT license are retained here. See `manifest.json`
for the semantic mapping and `LICENSE` for the upstream copyright and license.

The settings and management entry glyphs use these official outlines, recolored to #2563EB.
The original viewport and padding are preserved, with no gradients, bevels or app tile backgrounds.
Docker and Git retain the existing recognizable brand artwork.

Regenerate the shared 192px transparent PNGs under `Assets/Icons/Fluent/` with:

```powershell
node Tools/Mobile/render-fluent-icons.cjs
python Tools/Mobile/sync-desktop-icons.py
python Tools/Mobile/sync-desktop-icons.py --check
```

The renderer requires `sharp` (resolvable via Node's usual module paths or `NODE_PATH`).
Rendering and Android synchronization use only checked-in assets and require no network access.
The Android APK includes the MIT license at `res/raw/fluent_system_icons_license.txt`.
