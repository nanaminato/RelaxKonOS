// Render the checked-in Microsoft SVG snapshot. Requires Node.js and sharp.
// NODE_PATH may point to an existing sharp installation; no download occurs here.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');

async function main() {
    const assets = path.resolve(__dirname, '../../Client/RelaxKonOS.Client/Assets');
    const source = path.join(assets, 'IconSources/FluentSystemIcons');
    const target = path.join(assets, 'Icons/Fluent');
    const manifest = JSON.parse(await fs.readFile(path.join(source, 'manifest.json'), 'utf8'));
    await fs.mkdir(target, { recursive: true });
    for (const [meaning, icon] of Object.entries(manifest.icons)) {
        const svg = (await fs.readFile(path.join(source, icon.file), 'utf8'))
            .replaceAll('fill="#212121"', `fill="${manifest.color}"`);
        // Preserve Microsoft's 24px viewport and padding, with no crop or distortion.
        await sharp(Buffer.from(svg), { density: 576 })
            .resize(manifest.size, manifest.size)
            .png().toFile(path.join(target, `${meaning}.png`));
    }
    console.log(`Rendered ${Object.keys(manifest.icons).length} Fluent icons.`);
}

main().catch(error => { console.error(error); process.exitCode = 1; });
