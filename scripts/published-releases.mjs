// Public README metadata, derived only from validated, merged release catalogs.
import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export const publishedPlugins = {
    AccountHub: 'bc9c3d3e-6da3-4ee5-91cd-c429d58ad901',
    Notifications: 'c18d78ec-4b70-47cf-a0d2-936d987c37ba',
    DiscordBridge: '4b047046-a550-4415-b204-4206f1f18201',
    DeletionQueue: '3de88ba1-6d5c-49f5-854b-65a72f473f21',
    AutomaticDeletionRequests: 'a6429fb2-c67c-459d-b6a7-579c4a05dace',
    PrivateSyncPlay: '09514a9e-f7a0-4c5a-91bc-3a46338298ca'
};
export const publishedRuntimes = ['linux-x64', 'linux-arm64', 'win-x64'];
const validVersion = value => typeof value === 'string' && /^\d+\.\d+\.\d+\.\d+$/.test(value);
const compare = (a, b) => {
    const left=a.version.split('.').map(Number),right=b.version.split('.').map(Number);
    for(let index=0;index<4;index++)if(left[index]!==right[index])return right[index]-left[index];
    return 0;
};
export function publishedSummary(catalogs, repository) {
    if(!/^[\w.-]+\/[\w.-]+$/.test(repository))throw Error('Invalid repository');
    const result={};
    for(const [name,guid]of Object.entries(publishedPlugins)){
        const versions=publishedRuntimes.map(runtime=>{
            const catalog=catalogs[runtime];if(!Array.isArray(catalog))throw Error('Missing platform catalog');
            const entries=catalog.filter(entry=>entry.guid?.toLowerCase()===guid);
            if(entries.length>1)throw Error('Duplicate plugin identity');
            if(!entries.length)return null;
            if(!Array.isArray(entries[0].versions)||!entries[0].versions.length)throw Error('Missing published history');
            const history=entries[0].versions;
            if(history.some(item=>!validVersion(item.version)||!validVersion(item.targetAbi)))throw Error('Invalid published version or ABI');
            return [...history].sort(compare)[0];
        });
        if(versions.every(item=>item===null))continue; // Not published yet.
        if(versions.some(item=>!item||item.version!==versions[0].version||item.targetAbi!==versions[0].targetAbi))throw Error('Platform release metadata disagrees');
        const latest=versions[0],source=new URL(latest.sourceUrl),prefix='/'+repository+'/releases/download/';
        if(source.origin!=='https://github.com'||!source.pathname.startsWith(prefix))throw Error('Unexpected release asset origin');
        const tag=decodeURIComponent(source.pathname.slice(prefix.length).split('/')[0]);
        if(!/^(?:[a-z-]+\/)?v\d+\.\d+\.\d+$/.test(tag))throw Error('Invalid published release tag');
        result[name]={version:latest.version,targetAbi:latest.targetAbi,releaseUrl:'https://github.com/'+repository+'/releases/tag/'+encodeURIComponent(tag)};
    }
    return result;
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
    const args=Object.fromEntries(process.argv.slice(2).map(value=>{const match=/^--(directory|repository|output)=(.+)$/.exec(value);if(!match)throw Error('Unknown published-summary option');return [match[1],match[2]];}));
    if(!args.directory||!args.repository||!args.output)throw Error('Directory, repository and output are required');
    const catalogs=Object.fromEntries(await Promise.all(publishedRuntimes.map(async runtime=>[
        runtime,JSON.parse(await readFile(resolve(args.directory,'manifest-'+runtime+'.json'),'utf8'))
    ])));
    await writeFile(args.output,JSON.stringify(publishedSummary(catalogs,args.repository),null,2)+'\n');
}
