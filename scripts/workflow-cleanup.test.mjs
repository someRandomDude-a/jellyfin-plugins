import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const read=path=>readFile(new URL('../'+path,import.meta.url),'utf8');

function assertLibraryFixtures(integration){
    const commands=integration.split(/\r?\n/).map(line=>line.trim()).filter(line=>line.startsWith('node scripts/integration.mjs '));
    assert(commands.some(line=>line.endsWith('--plugins=discord --security=latest --proof')));
    assert(commands.some(line=>line.endsWith('--plugins=discord,notifications --security=latest --library')));
    for(const line of commands.filter(line=>line.includes('--library'))){
        const plugins=/--plugins=([^\s]+)/.exec(line)?.[1];
        if(plugins)assert(plugins.split(',').includes('notifications'),'Every selected library fixture must install Notifications: '+line);
    }
    assert(integration.includes('--security=latest --wiring-check'));
}

test('tool-bound child workflows do not install the same Node/SDK twice',async()=>{
    for(const name of ['tests','plugin-checks','integration','release','release-all']){
        const workflow=await read('.github/workflows/'+name+'.yml');
        assert(workflow.includes('uses: ./.github/actions/use-latest-targets'),name);
        assert(!workflow.includes('uses: actions/setup-dotnet@'),name);
        assert(!workflow.includes('uses: actions/setup-node@'),name);
    }
    const binding=await read('.github/actions/use-latest-targets/action.yml');
    assert(binding.includes('uses: ./.github/actions/setup-latest-tools'));
    assert(binding.includes('scripts/prepare-test-targets.mjs'));
});
test('preview updates serialize without publication and Hub-only fixtures do not claim library ownership',async()=>{
    const preview=await read('.github/workflows/dependency-bump.yml');
    assert(preview.includes('dependency-bump-preview-${{ github.ref }}'));
    assert(preview.includes('cancel-in-progress: false'));
    assert(preview.includes('contents: read'));
    assert(!preview.includes('git push'));
    const integration=await read('.github/workflows/integration.yml');
    assertLibraryFixtures(integration);
});
test('library fixture guards accept LF and CRLF while still rejecting a missing Notifications owner',async()=>{
    const integration=(await read('.github/workflows/integration.yml')).replace(/\r\n/g,'\n');
    for(const newline of ['\n','\r\n']){
        const text=integration.replace(/\n/g,newline);
        assertLibraryFixtures(text);
        assert.throws(()=>assertLibraryFixtures(text.replace('--plugins=discord,notifications','--plugins=discord')));
        assert.throws(()=>assertLibraryFixtures(text+newline+'node scripts/integration.mjs --plugins=hub --library'+newline),/Notifications/);
    }
});
test('current status stays concise and preserves detailed evidence separately',async()=>{
    const status=await read('docs/review-status.md');
    assert(status.split('\n').length<100);
    assert(status.includes('review-history.md'));
    assert(status.includes('Remaining acceptance'));
    assert((await read('docs/review-history.md')).includes('Historical review log'));
});
