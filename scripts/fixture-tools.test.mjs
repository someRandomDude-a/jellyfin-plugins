import test from 'node:test';
import assert from 'node:assert/strict';
import {requireFixturePowerShell} from './fixture-tools.mjs';

test('installed fixtures reject missing or broken PowerShell before spawning a matrix',()=>{
    for(const result of [{error:{code:'ENOENT'},status:null},{status:1,stdout:''},{status:null,stdout:''}])assert.throws(()=>requireFixturePowerShell('missing',()=>result),/PowerShell is unavailable/);
});
test('fixture PowerShell must support CommandWithArgs and cannot silently fall back to Windows PowerShell',()=>{
    for(const version of ['5.1.19041','7.3.12','unexpected'])assert.throws(()=>requireFixturePowerShell('test',()=>({status:0,stdout:version})),/PowerShell 7.4/);
    for(const version of ['7.4.0','7.6.9\r\n','8.0.0'])assert.equal(requireFixturePowerShell('validated',()=>({status:0,stdout:version})),'validated');
});
