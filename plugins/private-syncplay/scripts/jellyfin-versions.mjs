import { appendFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { resolveTarget } from './jellyfin-target.mjs';

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const target=await resolveTarget();
    if(process.env.GITHUB_OUTPUT) await appendFile(process.env.GITHUB_OUTPUT,`latest=${target.tag}\nimage=${target.image}\n`);
    console.log(`Latest stable Jellyfin ${target.tag}; SDK ${target.version}; target ABI ${target.abi}; image ${target.image}`);
}
