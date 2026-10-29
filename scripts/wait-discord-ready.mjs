// Operator handoff only: never reads .env or bot credentials. Wait for a single
// newly owned fixture's readiness event without polling its progress log.
import { watch } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { resolve, dirname, basename } from 'node:path';
import assert from 'node:assert/strict';
const file=resolve(process.argv[2]||'');assert(process.argv[2],'A unique owned readiness path is required');
const result=await new Promise((resolve,reject)=>{
    let complete=false;
    const finish=(error,value)=>{if(complete)return;complete=true;watcher.close();clearTimeout(deadline);error?reject(error):resolve(value);};
    const check=async()=>{try{const data=JSON.parse(await readFile(file,'utf8'));if(data.status==='ready')finish(null,data);else if(data.status==='failed')finish(Error('Disposable Discord setup failed at '+String(data.stage||'unknown stage')+'; inspect sanitized diagnostics.'));}catch(error){if(error.code!=='ENOENT'&&!(error instanceof SyntaxError))finish(error);}};
    const watcher=watch(dirname(file),(_,name)=>{if(String(name)===basename(file))void check();});
    const deadline=setTimeout(()=>finish(Error('The disposable Discord fixture did not become ready in time; inspect its sanitized setup log.')),600000);
    watcher.on('error',error=>finish(error));void check();
});
assert(new URL(result.publicBaseUrl).protocol==='https:');
console.log(JSON.stringify(result,null,2));
