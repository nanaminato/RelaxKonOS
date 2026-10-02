# Desktop icon refresh

Generated with the built-in `image_gen` tool on 2026-10-02 using `Assets/AppIcons/settings.png` as a style reference. Each icon was generated separately with a transparent background and resized to the existing 192 × 192 RGBA asset size; no runtime image generation or network access is needed.

Final desktop assets:

- `Assets/AppIcons/launchpad.png`: blue tile with a nine-cell launcher grid.
- `Assets/AppIcons/taskmanager.png`: navy tile with a monitor and cyan performance waveform.
- `Assets/AppIcons/event-alerts.png`: amber tile with a bell and alert mark.

Android resources are derived from the desktop assets with `tools/Mobile/sync-desktop-icons.py`.

## Prompt set

Every subject below was appended to this shared prompt:

> Use case: stylized-concept. Asset type: production desktop application icon, one square PNG with a genuinely transparent background. Match the provided settings icon only as a STYLE REFERENCE: rounded square tile, restrained soft 3D bevel, clean centered recognizable pictogram, top-left light, smooth subtle gradients, polished modern desktop icon, legible at 24/32/48px. Tile occupies 90% of square, centered, equal transparent margins. Do not draw other tiles, captions, letters, watermarks, checkerboard, outer background, huge glow or perspective tilt. Generate a NEW icon; do not preserve the gear subject.

Launchpad:

> Subject: Launchpad app launcher. Deep indigo to blue rounded square tile, nine small raised rounded squares in a precise 3 by 3 grid, ivory and pale sky blue squares with one soft cyan accent. Simple elegant launcher symbol, consistent spacing and no tiny detail.

Task Manager:

> Subject: Task Manager system performance monitor. Deep navy rounded square tile, centered light silver monitor frame with a dark blue display containing a vivid cyan performance waveform and three simple short bars. Distinct readable silhouette, minimal fine detail, attractive at small size.

Event & Alert Center:

> Subject: Event and Alert Center. Warm amber to orange rounded square tile, centered large ivory bell with a small dark amber exclamation cut into the bell and a small clapper. Calm clear notification/operations alert symbol, recognizable without a bare warning triangle, no red badge or text.
