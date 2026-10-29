import {rename} from 'node:fs/promises';
import {resolve,relative,sep} from 'node:path';

// Only stopped, newly owned integration fixtures may use this helper. Docker
// owns some Linux parent directories; move files as that owner, without chmod,
// deleting state, following a fallback path, or modifying an operator install.
export async function moveFixtureState(config,source,destination,{container,docker,move=rename}){
    const root=resolve(config),src=relative(root,resolve(source)).split(sep).join('/'),dst=relative(root,resolve(destination)).split(sep).join('/');
    const allowed=/^(plugins\/configurations\/AccountHub\/account-hub\.db(?:-wal|-shm|-journal)?|data\/automatic-deletion-requests\/rules\.json)(?:\.preserved|\.unexpected)?$/;
    const a=allowed.exec(src),b=allowed.exec(dst);
    if(!a||!b||a[1]!==b[1]||src===dst)throw Error('Refusing an unscoped fixture state move');
    try{return await move(source,destination);}catch(error){if(!['EACCES','EPERM'].includes(error.code))throw error;}
    if(!/^jellyfin-suite-test-[a-f0-9]{8}$/.test(container)||docker('inspect','--format','{{.State.Running}}',container)!=='false')throw Error('Fixture state moves require the owned server to be stopped');
    const script='import os,pathlib,sys\nroot=pathlib.Path("/fixture")\na=root/sys.argv[1]\nb=root/sys.argv[2]\nassert not a.is_symlink() and not b.exists()\nassert a.resolve().is_relative_to(root) and b.parent.resolve().is_relative_to(root)\nos.rename(a,b)';
    docker('run','--rm','--network=none','--read-only','--mount',`type=bind,source=${root},target=/fixture`,'python:alpine','python','-c',script,src,dst);
}
