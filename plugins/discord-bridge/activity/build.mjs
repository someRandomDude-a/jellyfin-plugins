import { build } from 'esbuild';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
await mkdir('dist', { recursive: true });
const result = await build({ entryPoints: ['src/main.mjs'], bundle: true, format: 'esm', platform: 'browser', target: 'es2022', outfile: 'dist/activity.js', sourcemap: false, legalComments: 'eof', metafile: true });
const packages = new Set(Object.keys(result.metafile.inputs).map(path => path.match(/^node_modules\/(\@[^/]+\/[^/]+|[^/]+)/)?.[1]).filter(Boolean));
const inventory = [], notices = [];
for (const name of packages) {
    const root = 'node_modules/' + name;
    const metadata = JSON.parse(await readFile(root + '/package.json', 'utf8'));
    // The SDK's published output is itself prebundled. Include its declared
    // runtime dependency licenses, not just files visible to esbuild's graph.
    for (const dependency of Object.keys(metadata.dependencies || {})) if (!dependency.startsWith('@types/')) packages.add(dependency);
    const licenseName = (await readdir(root)).find(file => /^licen[cs]e(?:\.md|\.txt)?$/i.test(file));
    if (!licenseName || !metadata.license) throw Error('Missing bundled dependency license: ' + name);
    notices.push(`${metadata.name} ${metadata.version} (${metadata.license})\n${await readFile(root + '/' + licenseName, 'utf8')}`);
    inventory.push({ name: metadata.name, version: metadata.version, license: metadata.license });
}
await writeFile('dist/embedded-app-sdk.LICENSE', notices.join('\n\n---\n\n'));
await writeFile('dist/activity-inventory.json', JSON.stringify({ bundleSha256: createHash('sha256').update(await readFile('dist/activity.js')).digest('hex'), packages: inventory }, null, 2) + '\n');
