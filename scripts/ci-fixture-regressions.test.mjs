import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdir,mkdtemp,rm,readFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {moveFixtureState} from './fixture-state-files.mjs';

test('snapshot tests work alone on a fresh runner without an opencode temp parent',async()=>{
    const base=join(tmpdir(),'opencode');await mkdir(base,{recursive:true});const directory=await mkdtemp(join(base,'fresh-runner-'));
    const env={...process.env,TMPDIR:directory,TEMP:directory,TMP:directory};delete env.NODE_TEST_CONTEXT;
    try{const result=spawnSync(process.execPath,['--test','--test-reporter=spec',fileURLToPath(new URL('./latest-releases.test.mjs',import.meta.url))],{encoding:'utf8',env,timeout:30000});assert.equal(result.status,0,result.stdout+result.stderr);assert(result.stdout.includes('children use the same run snapshot'),result.stdout+result.stderr);}finally{await rm(directory,{recursive:true,force:true});}
});
test('Queue archive allowlist matches staged dependencies, including diagnostics',async()=>{
    const read=path=>readFile(new URL('../plugins/deletion-queue/scripts/'+path,import.meta.url),'utf8');
    const pack=await read('package.ps1'),check=await read('release-common.ps1');
    const names=code=>[...code.matchAll(/'([^']+\.(?:dll|json))'/g)].map(value=>value[1]).sort();
    const staged=names(pack.match(/\$keep = @\(([^]*?)\)\s*foreach/)[1]);
    const expected=names(check.match(/\$expected = @\(([^]*?)\)\s*\$archive/)[1]).filter(value=>value!=='meta.json');
    assert.deepEqual(expected,staged);assert(expected.includes('Jellyfin.Diagnostics.dll'));
});
test('Linux permission fallback stays offline and limited to stopped owned fixture state',async()=>{
    const root=resolve('owned-fixture/config'),source=join(root,'plugins/configurations/AccountHub/account-hub.db'),calls=[];
    const docker=(...args)=>{calls.push(args);return args[0]==='inspect'?'false':'';};
    await moveFixtureState(root,source,source+'.preserved',{container:'jellyfin-suite-test-1234abcd',docker,move:async()=>{throw Object.assign(Error(),{code:'EACCES'});}});
    assert.equal(calls.length,2);assert(calls[1].includes('--network=none'));assert(calls[1].includes('--read-only'));assert(calls[1].includes('plugins/configurations/AccountHub/account-hub.db.preserved'));
    await assert.rejects(moveFixtureState(root,resolve('../outside.db'),source,{container:'jellyfin-suite-test-1234abcd',docker}),/unscoped/);
    await assert.rejects(moveFixtureState(root,source,source+'.preserved',{container:'jellyfin-suite-test-1234abcd',docker:()=> 'true',move:async()=>{throw Object.assign(Error(),{code:'EPERM'});}}),/stopped/);
    await assert.rejects(moveFixtureState(root,source,source+'.preserved',{container:'jellyfin-suite-test-1234abcd',docker,move:async()=>{throw Object.assign(Error(),{code:'ENOENT'});}}),{code:'ENOENT'});
});
