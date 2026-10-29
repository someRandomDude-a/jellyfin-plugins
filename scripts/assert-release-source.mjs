import {pathToFileURL} from 'node:url';

export function assertReleaseSource(selected,tagged){
    if(![selected,tagged].every(value=>/^[a-f0-9]{40}$/.test(value||'')))throw Error('Cannot verify exact release source commits');
    if(selected!==tagged)throw Error('Existing immutable release tag points to older or different source. Choose an unused version for the fixes, or explicitly select the original tagged commit to rerun it. Never move or overwrite the tag.');
}
if(process.argv[1]&&import.meta.url===pathToFileURL(process.argv[1]).href){
    if(process.argv.length!==4)throw Error('Expected selected and tagged commit IDs');
    assertReleaseSource(process.argv[2],process.argv[3]);
}
