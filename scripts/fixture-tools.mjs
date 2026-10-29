import {spawnSync} from 'node:child_process';

// Fail before creating fixtures instead of reporting dozens of product failures
// when an external temporary tool has disappeared during a long matrix run.
export function requireFixturePowerShell(executable=process.env.PWSH||'pwsh',run=spawnSync){
    const result=run(executable,['-NoLogo','-NoProfile','-Command','$PSVersionTable.PSVersion.ToString()'],{encoding:'utf8',timeout:30000});
    if(result.error||result.status!==0)throw Error('Fixture PowerShell is unavailable. Set PWSH to a working PowerShell 7.4+ executable before running installed tests.');
    const match=/^(\d+)\.(\d+)\.(\d+)(?:\s|$)/.exec(result.stdout.trim());
    if(!match||Number(match[1])<7||Number(match[1])===7&&Number(match[2])<4)throw Error('Installed fixtures require PowerShell 7.4+ for CommandWithArgs; Windows PowerShell 5 is not supported.');
    return executable;
}
