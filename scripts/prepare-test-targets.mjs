// Internal bootstrap used automatically by test/package entry points and CI.
import { readFile, writeFile, appendFile, mkdir } from 'node:fs/promises';
import { resolve, dirname, join } from 'node:path';
import { loadTargets, saveTargets, discoverTargets, downloadSecurity, pullImage, jellyfinTarget, githubJson } from './latest-releases.mjs';
import { checkSourceContract } from './reviewed-contracts.mjs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const option = name => process.argv.find(value => value.startsWith('--' + name + '='))?.slice(name.length + 3);
const output = resolve(option('output') || 'artifacts/test-targets.json');
const names = (option('services') || 'jellyfin,security,enhanced,seerr,sonarr,radarr').split(',');
const inherited = process.env.SUITE_TEST_TARGETS;
const snapshot = inherited ? JSON.parse(await readFile(inherited, 'utf8')) : await discoverTargets(names);
if (process.argv.includes('--record-toolchain')) {
    if (inherited) throw Error('Cannot replace the parent workflow toolchain evidence');
    const powershell=execFileSync(process.env.PWSH||'pwsh',['-NoLogo','-NoProfile','-Command','$PSVersionTable.PSVersion.ToString()'],{encoding:'utf8',stdio:['ignore','pipe','pipe']}).trim();
    if(!/^\d+\.\d+\.\d+$/.test(powershell))throw Error('Missing concrete verified PowerShell target');
    snapshot.tools = { node: process.versions.node, powershell, dotnet: JSON.parse(await readFile(new URL('../global.json', import.meta.url), 'utf8')).sdk.version };
}
if (inherited) await loadTargets(names);
if (process.env.GITHUB_ACTIONS === 'true' && snapshot.tools) {
    if (process.versions.node !== snapshot.tools.node) throw Error('LATEST-ONLY CI BLOCKED: Node runtime differs from the shared workflow target');
    if (process.env.VERIFY_BUILD_SDK === 'true') {
        let actual;
        try { actual = execFileSync('dotnet', ['--version'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim(); }
        catch { throw Error('LATEST-ONLY CI BLOCKED: installed .NET SDK differs or is unavailable; install the exact tested workflow target'); }
        if (actual !== snapshot.tools.dotnet) throw Error('LATEST-ONLY CI BLOCKED: installed .NET SDK differs from the tested workflow target');
    }
    let powershell;
    try{powershell=execFileSync(process.env.PWSH||'pwsh',['-NoLogo','-NoProfile','-Command','$PSVersionTable.PSVersion.ToString()'],{encoding:'utf8',stdio:['ignore','pipe','pipe']}).trim();}
    catch{throw Error('LATEST-ONLY CI BLOCKED: verified PowerShell is unavailable');}
    if(powershell!==snapshot.tools.powershell)throw Error('LATEST-ONLY CI BLOCKED: PowerShell differs from the shared workflow target');
}
const targets = snapshot.targets;
const target = jellyfinTarget(targets.jellyfin);
if (!targets.jellyfin.contractFingerprint) Object.assign(targets.jellyfin, await checkSourceContract(targets.jellyfin, 'native'));
let securityVersion = '';
if (targets.security) {
    if (!targets.security.contractFingerprint) Object.assign(targets.security, await checkSourceContract(targets.security, 'security'));
    if (!targets.security.contractReviewed) throw Error('Latest Security source contract changed; refusing automatic compatibility approval');
    if (!targets.security.assetAssemblyVersion) {
        // Release ZIP naming/source Version need not equal its binary revision.
        // Bind approval to the discovered, checksum-verified official artifact.
        targets.security.localPath = await downloadSecurity(targets.security, join(dirname(output), 'security-latest.zip'));
        targets.security.assetAssemblyVersion = execFileSync(process.env.PWSH || 'pwsh', ['-NoProfile', '-File', fileURLToPath(new URL('./read-security-version.ps1', import.meta.url)), '-ZipPath', targets.security.localPath, '-Sha256', targets.security.sha256], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
    }
    securityVersion = targets.security.assetAssemblyVersion;
    if (!/^\d+\.\d+\.\d+\.\d+$/.test(securityVersion || '')) throw Error('Latest Security assembly version unavailable');
    targets.security.assemblyVersion = securityVersion;
}
// Read matching upstream dependency versions, not separately pinned EF/SQLite.
if (!targets.jellyfin.databasePackageVersion || !targets.jellyfin.skiaPackageVersion) {
    const content = await githubJson('repos/jellyfin/jellyfin/contents/Directory.Packages.props?ref=' + targets.jellyfin.sourceCommit);
    const props = Buffer.from(content.content, 'base64').toString('utf8');
    const sqlite = props.match(/Include="Microsoft.Data.Sqlite(?:.Core)?"\s+Version="([\d.]+)"/)?.[1];
    const ef = props.match(/Include="Microsoft.EntityFrameworkCore.Sqlite"\s+Version="([\d.]+)"/)?.[1];
    if (!sqlite || sqlite !== ef) throw Error('Latest Jellyfin EF/SQLite dependency contract unavailable');
    targets.jellyfin.databasePackageVersion = sqlite;
    const skia = props.match(/Include="SkiaSharp"\s+Version="([\d.]+)"/)?.[1];
    if (!skia) throw Error('Latest Jellyfin renderer dependency contract unavailable');
    targets.jellyfin.skiaPackageVersion = skia;
}
const buildProps = resolve(dirname(output), 'Jellyfin.Latest.props');
await mkdir(dirname(buildProps), { recursive: true });
await writeFile(buildProps, `<Project><PropertyGroup><JellyfinVersion>${target.version}</JellyfinVersion><JellyfinServerTag>${target.tag}</JellyfinServerTag><JellyfinTargetAbi>${target.abi}</JellyfinTargetAbi><JellyfinDatabasePackageVersion>${targets.jellyfin.databasePackageVersion}</JellyfinDatabasePackageVersion><JellyfinSkiaPackageVersion>${targets.jellyfin.skiaPackageVersion}</JellyfinSkiaPackageVersion><ReviewedNativeDatabaseAbi>${targets.jellyfin.contractReviewed ? target.abi : ''}</ReviewedNativeDatabaseAbi><ReviewedSecurityVersion>${securityVersion}</ReviewedSecurityVersion></PropertyGroup></Project>\n`);
if (process.argv.includes('--pull')) for (const name of names) if (targets[name].image && !targets[name].imageDigest) targets[name] = pullImage(targets[name], option('platform') || 'linux/amd64');
if (process.argv.includes('--download-security')) {
    if (!targets.security) throw Error('Security discovery is required');
    targets.security.localPath = await downloadSecurity(targets.security, join(dirname(output), 'security-latest.zip'));
}
await saveTargets(output, targets, snapshot);
if (process.env.GITHUB_ENV) await appendFile(process.env.GITHUB_ENV, `SUITE_TEST_TARGETS=${output}\nSUITE_TEST_RUN_ID=${snapshot.runId}\nSUITE_JELLYFIN_PROPS=${buildProps}\n`);
if (process.env.GITHUB_ENV) for (const [name, env] of [['jellyfin', 'JELLYFIN_TEST_IMAGE'], ['seerr', 'SEERR_TEST_IMAGE']]) {
    if (targets[name]?.image) await appendFile(process.env.GITHUB_ENV, `${env}=${targets[name].imageDigest || targets[name].image}\n`);
}
for (const [name, latest] of Object.entries(targets)) console.log(name + ': latest stable ' + latest.version);
console.log('Per-run target evidence: ' + output);
