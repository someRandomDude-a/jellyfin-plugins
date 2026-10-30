import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir, access } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { plugins, commonTests, ownerTests, mixedTests, testProjects, testPlan } from './test-plan.mjs';
const read = path => readFile(new URL('../' + path, import.meta.url),'utf8');
const nodeTests = plan => plan.filter(step=>step.program==='node'&&step.arguments.includes('--test'));
test('suite tests the solution once, batches common/frontend tests once and retains package/SDK gates',()=>{
    const plan=testPlan(),dotnet=plan.filter(step=>step.program==='dotnet');
    assert.equal(dotnet.length,1);assert(dotnet[0].arguments.includes('Jellyfin.Plugins.slnx'));
    assert.equal(new Set(plan.map(step=>JSON.stringify(step))).size,plan.length);
    const frontend=nodeTests(plan).filter(step=>step.directory==='.');assert.equal(frontend.length,1);
    assert(!frontend[0].arguments.some(arg=>arg.startsWith('--test-name-pattern')));
    for(const path of [...commonTests,...Object.values(ownerTests).flat(),...mixedTests])assert.equal(frontend[0].arguments.filter(arg=>arg===path).length,1,path);
    assert(plan.some(step=>step.arguments.includes('scripts/test-sdk.ps1')));
    assert(plan.some(step=>step.arguments.includes('scripts/test-release.ps1')));
    assert(!plan.some(step=>step.arguments.includes('-ProtocolOnly')),'The full SyncPlay catalog test already includes the shared protocol cases');
});
test('selected plans compile only the owner and real dependencies, and select UI cases by explicit owner',()=>{
    for(const owner of plugins){
        const plan=testPlan(owner);
        assert.deepEqual(plan.filter(step=>step.program==='dotnet').map(step=>step.arguments[1]),testProjects[owner]);
        const root=nodeTests(plan).filter(step=>step.directory==='.'),batch=root.find(step=>!step.arguments.some(arg=>arg.startsWith('--test-name-pattern')));
        for(const path of [...commonTests,...ownerTests[owner]])assert(batch.arguments.includes(path),owner+': '+path);
        for(const other of plugins.filter(value=>value!==owner))for(const path of ownerTests[other])assert(!batch.arguments.includes(path),owner+' must not run '+other+' UI implementation tests');
        assert(root.some(step=>step.arguments.includes('--test-name-pattern=^'+owner+': ')));
        if(owner==='private-syncplay'){
            assert(!root.some(step=>step.arguments.includes('scripts/web-ui.test.mjs')));
            assert(root.some(step=>step.arguments.includes('scripts/ui-safety-followup.test.mjs')));
        }
    }
    assert.throws(()=>testPlan('unknown'),/Unknown test owner/);
});
test('every current root test and solution test project has exactly one owner or common registry entry',async()=>{
    const registered=[...commonTests,...Object.values(ownerTests).flat(),...mixedTests];
    assert.equal(registered.length,new Set(registered).size);
    const actual=(await readdir(new URL('./',import.meta.url))).filter(name=>name.endsWith('.test.mjs')).map(name=>'scripts/'+name).sort();
    assert.deepEqual(registered.filter(path=>path.startsWith('scripts/')).sort(),actual,'Assign new tests explicitly; never silently omit them from normal CI');
    for(const path of registered)await access(new URL('../'+path,import.meta.url));
    const solution=await read('Jellyfin.Plugins.slnx'),projects=[...new Set(Object.values(testProjects).flat())].sort();
    assert.deepEqual([...solution.matchAll(/Path="([^"]+Tests\.csproj)"/g)].map(match=>match[1]).sort(),projects);
});
test('selected owner UI plans include formerly missed cases and exclude unrelated owners',()=>{
    const env={...process.env};delete env.NODE_TEST_CONTEXT;
    for(const [owner,file,expected] of [
        ['automatic-deletion-requests','web-ui','finished-viewer lists'],
        ['notifications','web-ui','Opening the catalog before initial settings'],
        ['private-syncplay','ui-safety-followup','SyncPlay nested dialogs']
    ]){
        const path=fileURLToPath(new URL('./'+file+'.test.mjs',import.meta.url));
        const result=spawnSync(process.execPath,['--test','--test-reporter=spec','--test-name-pattern=^'+owner+': ',path],{env,encoding:'utf8',timeout:30000});
        assert.equal(result.status,0,result.stdout+result.stderr);assert(result.stdout.includes(expected),result.stdout+result.stderr);
        // The spec reporter prints only executed tests, not skipped foreign owners.
        assert(!/✔ (?:account-hub|discord-bridge|deletion-queue):/.test(result.stdout));
    }
});
test('PowerShell runner executes the shared plan instead of maintaining a second suite registry',async()=>{
    const runner=await read('scripts/run-tests.ps1');
    assert(runner.includes('test-plan.mjs'));assert(runner.includes('[switch]$List'));
    assert(!runner.includes('switch ($selectedPlugin)'));assert(!runner.includes('Jellyfin.Plugins.slnx'));
    assert((await read('scripts/test.ps1')).includes('Invoke-TestEntryPoint -Plugin $Plugin'));
});
