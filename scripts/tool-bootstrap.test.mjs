import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const read=path=>readFile(new URL('../'+path,import.meta.url),'utf8');
test('tool bootstrap resolves official stable metadata and verifies downloads before extraction',async()=>{
    const code=await read('scripts/bootstrap-tools.ps1');
    for(const value of ['nodejs.org/dist/index.json','PowerShell/PowerShell/releases/latest','releases-index.json','SHASUMS256.txt','Get-FileHash','SHA512','checksum mismatch','PlanOnly','SUITE_TOOLCHAIN_READY'])assert(code.includes(value),value);
    assert(code.indexOf('Get-FileHash')<code.indexOf('Expand-Archive'));assert(!code.includes('Invoke-Expression'));assert(code.includes('do not override the source SDK'));
});
test('test/release entry points install current tools and reject stale source before tagging',async()=>{
    for(const path of ['.github/workflows/release.yml','.github/workflows/release-all.yml']){const code=await read(path);assert(code.indexOf('./scripts/bootstrap-tools.ps1')<code.indexOf('git tag '));assert(code.indexOf('node scripts/check-dependency-freshness.mjs')<code.indexOf('git tag '));}
    assert((await read('.github/workflows/latest-targets.yml')).includes('uses: ./.github/actions/setup-latest-tools'));
    assert((await read('.github/actions/use-latest-targets/action.yml')).includes('uses: ./.github/actions/setup-latest-tools'));
    const entry=await read('scripts/test-targets.ps1');assert(entry.includes('bootstrap-tools.ps1'));assert(entry.includes('check-dependency-freshness.mjs'));
    assert((await read('.github/workflows/dependency-bump.yml')).includes('schedule:'));
});
