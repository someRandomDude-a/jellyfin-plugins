// Disposable, synthetic API acceptance only. No production credentials or media.
import { execFileSync, spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setTimeout } from 'node:timers/promises';
import { randomUUID } from 'node:crypto';
import { dirname, join } from 'node:path';
import { ensureTargets, assertRuntimeVersion, freshDockerArguments } from './latest-releases.mjs';
const docker=(...args)=>execFileSync('docker',freshDockerArguments(args),{encoding:'utf8',stdio:['ignore','pipe','pipe'],maxBuffer:2*1024*1024}).trim();
const targets=[['sonarr','8989'],['radarr','7878']];
const env={...process.env};
const releases = await ensureTargets(['jellyfin', 'sonarr', 'radarr'], { pull: ['sonarr', 'radarr'] });
// Bootstrap .NET against the same Jellyfin snapshot as this external fixture.
execFileSync(process.execPath, ['scripts/prepare-test-targets.mjs', '--services=jellyfin', '--output=' + process.env.SUITE_TEST_TARGETS], { env: process.env, stdio: 'pipe' });
env.SUITE_TEST_TARGETS = process.env.SUITE_TEST_TARGETS;
env.SUITE_TEST_RUN_ID = process.env.SUITE_TEST_RUN_ID;
env.SUITE_JELLYFIN_PROPS = join(dirname(process.env.SUITE_TEST_TARGETS), 'Jellyfin.Latest.props');
const suffix = randomUUID().slice(0, 8), owned = [];
// undici can wait on unreferenced handles during early API startup on Windows.
const keepAlive = setInterval(() => {}, 1000);
try {
for(const [source,port]of targets){
    const name='suite-metadata-'+source+'-'+suffix;
    docker('create', '--name', name, '--label', 'jellyfin.suite.metadata-test=' + suffix, '-p', '127.0.0.1::' + port, '-v', '/config', '--tmpfs', '/test-media', releases[source].imageDigest);
    owned.push(name); docker('start', name);
    let config;
    for (let attempt=0; attempt<90; attempt++) {
        try { config=docker('exec',name,'cat','/config/config.xml'); if (/<ApiKey>[^<]+<\/ApiKey>/.test(config)) break; } catch { /* Owned fixture is still starting. */ }
        await setTimeout(1000);
    }
    assert(config, 'Disposable configuration unavailable');
    const key=/<ApiKey>([^<]+)<\/ApiKey>/.exec(config)?.[1];assert(key,'Disposable API key unavailable');
    let base='http://'+docker('port',name,port+'/tcp');
    const api=async(path,body)=>{const response=await fetch(base+'/api/v3/'+path,{method:body?'POST':'GET',headers:{'X-Api-Key':key,'Content-Type':'application/json'},body:body?JSON.stringify(body):undefined,redirect:'error',signal:AbortSignal.timeout(15000)});if(!response.ok){const errors=await response.json();throw Error(source+' API '+path+' HTTP '+response.status+' '+JSON.stringify(Array.isArray(errors)?errors.map(e=>({field:e.propertyName,error:e.errorMessage})):errors));}return response.json();};
    let status;
    for (let attempt=0; attempt<90; attempt++) { try { status=await api('system/status'); break; } catch { await setTimeout(1000); } }
    assert(status, 'Disposable API did not start');
    assertRuntimeVersion(status.version, releases[source].version, source);
    console.log(source+' latest real API version '+status.version+' image '+docker('inspect','--format','{{.Image}}',name));
    docker('exec',name,'mkdir','-p','/test-media');docker('exec',name,'chown','1000:1000','/test-media');
    const profiles=await api('qualityprofile');assert(profiles.length,'Default quality profile unavailable');
    const roots=await api('rootfolder');if(!roots.some(r=>r.path==='/test-media'))await api('rootfolder',{path:'/test-media'});
    const kind=source==='sonarr'?'series':'movie';
    let rows=await api(kind);let row=rows.find(r=>r.title==='Suite synthetic metadata');
    if(!row){
        // Native APIs require external catalogue lookup on creation. Seed only
        // these disposable databases while stopped, then verify the real GET APIs.
        docker('stop',name);
        try{const seeded=spawnSync('docker',['run','--pull=always','--rm','-i','--network','none','--volumes-from',name,'python:alpine','python','-',source,String(profiles[0].id)],{input:readFileSync(new URL('./seed-arr-fixture.py',import.meta.url)),encoding:'utf8'});assert.equal(seeded.status,0,seeded.stderr);process.stdout.write(seeded.stdout);}finally{docker('start',name);base='http://'+docker('port',name,port+'/tcp');}
        let ready=false;for(let attempt=0;attempt<30;attempt++){try{await api('system/status');ready=true;break;}catch{await setTimeout(1000);}}assert(ready,'Disposable server did not restart');
        row=(await api(kind)).find(r=>r.title==='Suite synthetic metadata');
    }
    assert(row.id>0,'Synthetic record missing');
    env['SUITE_'+source.toUpperCase()+'_URL']=base;env['SUITE_'+source.toUpperCase()+'_KEY']=key;
}
const result=spawnSync('dotnet',['test','plugins/automatic-deletion-requests/tests/AutomaticDeletionRequests.Tests/AutomaticDeletionRequests.Tests.csproj','--nologo','--filter','FullyQualifiedName~RealArrTests'],{env,encoding:'utf8',maxBuffer:4*1024*1024});
process.stdout.write(result.stdout||'');process.stderr.write(result.stderr||'');assert.equal(result.status,0,'Real Arr provider acceptance failed');
console.log('PASS synthetic Sonarr/Radarr real API metadata provider reads; no destructive plugin operations.');
} finally {
    clearInterval(keepAlive);
    // Never inspect, stop or seed retained fixed-name/operator containers.
    for (const name of owned.reverse()) docker('rm', '-f', '-v', name);
}
