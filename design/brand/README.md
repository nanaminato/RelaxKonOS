# Sleeping calico brand

`sleeping-calico-master.png` is the transparent master for the desktop client,
Android client, and public website. Generated with the built-in imagegen tool
using the user's sleeping calico reference image.

Prompt: Redesign the reference sleeping calico into a compact curled kitten with
a large readable sleeping face on front paws and a tucked curled tail. Preserve
cream white, apricot orange, dark chocolate patches, pink nose, closed curved
eyes, and soft clay 3D styling. Simplify broad patches for small icons. One
centered cat, transparent background, clean silhouette, no text, tile, glow,
accessories, or external cast shadow.

To regenerate platform sizes (Python 3 with Pillow; sibling website checkout):

```powershell
python ../RelaxKon/tools/generate-brand-assets.py
python Tools/Mobile/sync-desktop-icons.py
python Tools/Mobile/sync-desktop-icons.py --check
```

From the website checkout, render the share card from its HTML source:

```powershell
node tools/generate-og-image.mjs
```

Desktop uses transparent PNG and multi-size ICO. Website launcher and Apple
icons use a cream background; the maskable icon reserves the central safe area.
Android resources are derived only by the existing desktop sync tool, preserving
the app's backdrop and adaptive icon safe area.
