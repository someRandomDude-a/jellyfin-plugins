import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { interactionsCompleted, accountInteractionsCompleted, accountResetCompleted, ownedInvitation, invitationInteractionsCompleted, invitationRedemptionCompleted, invitationHumanConfirmed } from './discord-acceptance.mjs';
test('Human account acceptance requires actual private reset delivery and a matching completed reset audit',()=>{
    const counters={'status:completed':1,'link.prepare:completed':1,'reset:completed':1};assert(accountInteractionsCompleted(counters));
    for(const key of Object.keys(counters))assert(!accountInteractionsCompleted({...counters,[key]:0}));
    const user='11111111-1111-1111-1111-111111111111',since=Date.parse('2026-10-27T00:00:00Z'),event={source:'account-hub',eventType:'password.reset',subjectId:user.replaceAll('-',''),occurredAt:'2026-10-27T00:01:00Z'};
    assert(accountResetCompleted([{event}],user,since));assert(!accountResetCompleted([{event:{...event,subjectId:'another'}}],user,since));assert(!accountResetCompleted([{event:{...event,eventType:'discord.linked'}}],user,since));assert(!accountResetCompleted([{event}],user,since+120000));
});
test('Interactive acceptance requires both explicit delivery paths and actual command outcomes', () => {
    const keys = ['help:completed', 'status:completed', 'link.prepare:completed', 'watchparty:delivered_dm', 'watchparty:delivered_channel'];
    const counters = Object.fromEntries(keys.map(key => [key, 1]));
    assert(interactionsCompleted(counters));
    for (const key of keys) assert(!interactionsCompleted({ ...counters, [key]: 0 }));
    assert(!interactionsCompleted({ connected: true }));
    assert(!interactionsCompleted({ ...counters, 'help:completed': '1' }));
});
test('Cleanup matches only this bot, time window, invitation prefix and fixture URL boundary', () => {
    const started = Date.parse('2026-10-24T00:00:00Z');
    const message = { author: { id: '123' }, timestamp: '2026-10-24T00:01:00Z', content: 'One-use Jellyfin watch-party invitation:\nhttps://fixture.example/suite/watch#secret' };
    const match = value => ownedInvitation(value, '123', started, 'https://fixture.example/suite');
    assert(match(message));
    assert(!match({ ...message, author: { id: '456' } }));
    assert(!match({ ...message, timestamp: '2026-10-23T00:00:00Z' }));
    assert(!match({ ...message, timestamp: 'invalid' }));
    assert(!match({ ...message, content: message.content.replace('/suite/', '/suite-other/') }));
    assert(!match({ ...message, content: message.content.replace('fixture.example', 'other.example') }));
    assert(!match({ ...message, content: 'unrelated https://fixture.example/suite/watch#secret' }));
    assert(match({...message,content:'Your one-time Jellyfin password reset (five minutes):\nhttps://fixture.example/suite/AccountHub/reset#token=synthetic'}));
});
test('Human invitation acceptance requires real handler outcomes including a rejected over-limit command',()=>{
    const keys=['help:completed','status:completed','link.prepare:completed','watchparty:delivered_dm','watchparty:delivered_channel','invite.create:completed','invite.create:rejected_limits'];
    const counters=Object.fromEntries(keys.map(key=>[key,1]));assert(invitationInteractionsCompleted(counters));
    for(const key of keys)assert(!invitationInteractionsCompleted({...counters,[key]:0}));
});
test('Signup acceptance requires the correct native creator, exact options and two completed reservations',()=>{
    const user='11111111-1111-1111-1111-111111111111',since=100000;
    const invite={createdBy:user.replaceAll('-',''),createdAt:since+100,maxUses:2,completedUses:2,reservedUses:2,expiresAt:since+100+3600000};
    assert(invitationRedemptionCompleted([invite],user,since));
    for(const patch of [{createdBy:'another'},{createdAt:since-1},{maxUses:3},{completedUses:1},{reservedUses:1},{expiresAt:invite.expiresAt+1}])assert(!invitationRedemptionCompleted([{...invite,...patch}],user,since));
});
test('Human-only party redemption and privacy checks cannot be fabricated by automated counters',()=>{
    const value={source:'human-confirmed',signupExtraUseDenied:true,partyChannelRedemption:true,partyDmRedemption:true,recipientPrivacy:true};
    assert(invitationHumanConfirmed(value));for(const key of Object.keys(value)){const partial={...value};delete partial[key];assert(!invitationHumanConfirmed(partial));}
    assert(!invitationHumanConfirmed({...value,source:'automated'}));
});
test('Human handoff rejects stale readiness or confirmation before live setup and preserves existing files',async()=>{
    const parent=join(tmpdir(),'opencode');await mkdir(parent,{recursive:true});const directory=await mkdtemp(join(parent,'handoff-guard-'));
    try{
        for(const suffix of ['', '.complete']){
            const ready=join(directory,'ready'+(suffix?'confirmation':'readiness')+'.json'),existing=ready+suffix;
            await writeFile(existing,'existing operator work');
            const result=spawnSync(process.execPath,[fileURLToPath(new URL('./integration.mjs',import.meta.url)),'--human-invitations','--interactive-discord','--ready-file='+ready],{encoding:'utf8',timeout:10000});
            assert.equal(result.error,undefined);assert.notEqual(result.status,0);
            assert.match(result.stderr,/requires absent readiness and confirmation files/);
            assert.equal(await readFile(existing,'utf8'),'existing operator work');
        }
    }finally{await rm(directory,{recursive:true,force:true});}
});
