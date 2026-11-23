import {readFile, writeFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import path from 'node:path';

const assets = fileURLToPath(new URL('../docs/assets/', import.meta.url));
// Coordinates use the original screenshot dimensions. Embed the original bytes
// so SVG images work offline and never redraw or alter Windows UI text.
const screenshots = [
    {source: 'extract-menu.jpg', output: 'extract-menu-annotated.svg', width: 358, height: 536,
        button: 'Extract All', box: [11, 110, 334, 33], arrow: [305, 215, 278, 148]},
    {source: 'extract-dialog.png', output: 'extract-dialog-annotated.svg', width: 614, height: 454,
        button: 'Extract', box: [447, 416, 82, 32], arrow: [412, 337, 481, 410]},
    {source: 'smartscreen-more-info.png', output: 'smartscreen-more-info-annotated.svg', width: 534, height: 500,
        button: 'More info', box: [15, 117, 78, 25], arrow: [169, 193, 89, 146]},
    {source: 'smartscreen-run-anyway.png', output: 'smartscreen-run-anyway-annotated.svg', width: 534, height: 500,
        button: 'Run anyway', box: [276, 433, 121, 47], arrow: [243, 346, 305, 426]},
];

for (const shot of screenshots) {
    const bytes = await readFile(path.join(assets, shot.source));
    const mime = shot.source.endsWith('.jpg') ? 'image/jpeg' : 'image/png';
    const [x, y, width, height] = shot.box;
    const [x1, y1, x2, y2] = shot.arrow;
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${shot.width}" height="${shot.height}" viewBox="0 0 ${shot.width} ${shot.height}" role="img" aria-labelledby="title description">
  <title id="title">${shot.button} button highlighted</title>
  <desc id="description">Original Windows screenshot with a red box around ${shot.button} and a red arrow pointing to it.</desc>
  <defs><marker id="arrowhead" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="18" markerHeight="18" orient="auto" markerUnits="userSpaceOnUse"><path d="M1 1 L11 6 L1 11 Z" fill="#ff0000"/></marker></defs>
  <image width="${shot.width}" height="${shot.height}" href="data:${mime};base64,${bytes.toString('base64')}"/>
  <rect x="${x}" y="${y}" width="${width}" height="${height}" rx="2" fill="none" stroke="#ff0000" stroke-width="4"/>
  <line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="#ff0000" stroke-width="5" stroke-linecap="round" marker-end="url(#arrowhead)"/>
</svg>
`;
    await writeFile(path.join(assets, shot.output), svg, 'utf8');
    console.log(`Annotated ${shot.button}`);
}
