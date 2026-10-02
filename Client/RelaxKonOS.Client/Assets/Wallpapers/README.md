# Built-in desktop photographs

These three photographs are bundled client resources. Selecting one stores only its
`builtin:` key in workspace preferences; no photograph is uploaded to the server or
fetched from Unsplash at runtime. Existing gradient presets remain selectable.

| Key | File | Photographer | Original photograph |
| --- | --- | --- | --- |
| `builtin:alpine-lake` (default) | `alpine-lake.jpg` | Yoshi Takekawa | [Blanca Lake](https://unsplash.com/photos/landscape-photography-of-mountain-surrounded-by-pine-trees-and-body-of-water-4qKVQYOluDk) |
| `builtin:ocean-waves` | `ocean-waves.jpg` | Paul Hanaoka | [Redondo Beach](https://unsplash.com/photos/aerial-photography-of-seashore-DKnXlH_r3x4) |
| `builtin:desert-dunes` | `desert-dunes.jpg` | Matt Artz | [Kelso Dunes](https://unsplash.com/photos/sand-dunes-at-daytime-ShQbbBBvoPM) |

All source pages identify the photographs as free under the
[Unsplash License](https://unsplash.com/license), verified on 2026-10-02.
The license permits free use, modification, and distribution, including commercial
use, with no required attribution. It restricts selling unmodified images and
compiling an image service that competes with Unsplash. These assets remain under
their original license, separately from RelaxKonOS's original code.

The bundled copies are JPEGs cropped by the source CDN to 2560 × 1440 at quality 85.
`sources.json` records download URLs and SHA-256 hashes for these exact files.

Verification (initializes the real Avalonia renderer without opening windows or
starting a client session):

```powershell
dotnet run --project Tests/Client/RelaxKonOS.Wallpaper.Tests
```
