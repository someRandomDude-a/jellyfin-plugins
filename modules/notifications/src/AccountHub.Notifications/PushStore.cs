using System.Text.Json;
using AccountHub.Api;

namespace AccountHub.Notifications;

public sealed partial class NotificationStore
{
    public PushConfiguration PushSettings()=>Run(c=>{
        var json=Scalar(c,"SELECT json FROM nh_push_settings WHERE id=1");return json is null?new(0,false,"",""):Decrypt<PushConfiguration>(json);
    });
    public void ConfigurePush(long revision,bool enabled,string? subject=null,bool? defaultOn=null)=>Run(c=>{
        using var tx=c.BeginTransaction(deferred:false);var json=Scalar(c,"SELECT json FROM nh_push_settings WHERE id=1");var current=json is null?WebPush.Create(0,false):Decrypt<PushConfiguration>(json);
        Validation.Require(current.Revision==revision,"Push configuration changed");subject??=current.Subject;
        Validation.Require(!enabled||subject is {Length:>0 and <=512}&&!subject.Any(char.IsControl)&&Uri.TryCreate(subject,UriKind.Absolute,out var contact)&&(contact.Scheme=="https"&&contact.UserInfo.Length==0||contact.Scheme=="mailto"&&System.Net.Mail.MailAddress.TryCreate(subject[7..],out _)),"A mailto: or HTTPS administrator contact is required for Web Push");
        var saved=current with{Revision=revision+1,Enabled=enabled,Subject=subject,DefaultOn=defaultOn??current.DefaultOn};Execute(c,"INSERT OR REPLACE INTO nh_push_settings VALUES(1,$0)",Encrypt(saved));
        if(!enabled){Execute(c,"DELETE FROM nh_push_jobs");Execute(c,"DELETE FROM nh_push_subscriptions");}tx.Commit();return 0;
    });
    public string SavePush(Guid user,PushSubscription subscription){WebPush.Validate(subscription);return Run(c=>{
        using var tx=c.BeginTransaction(deferred:false);var config=Scalar(c,"SELECT json FROM nh_push_settings WHERE id=1");Validation.Require(config is not null&&Decrypt<PushConfiguration>(config).Enabled,"Web Push is disabled");
        var id=Validation.Hash(subscription.Endpoint);var owner=Scalar(c,"SELECT user_id FROM nh_push_subscriptions WHERE id=$0",id);Validation.Require(owner is null||owner==user.ToString("D"),"Subscription belongs to another account; disable it there first");
        Validation.Require(owner is not null||int.Parse(Scalar(c,"SELECT COUNT(*) FROM nh_push_subscriptions WHERE user_id=$0",user)!)<8,"Device limit reached");
        Validation.Require(owner is not null||int.Parse(Scalar(c,"SELECT COUNT(*) FROM nh_push_subscriptions")!)<4000,"Subscription limit reached");
        Execute(c,"INSERT OR REPLACE INTO nh_push_subscriptions VALUES($0,$1,$2)",id,user,Encrypt(subscription));tx.Commit();return id;
    });}
    public PushTarget[] PushTargets(Guid user)=>Run(c=>{using var cmd=Command(c,"SELECT id,json FROM nh_push_subscriptions WHERE user_id=$0",user);using var rows=cmd.ExecuteReader();var result=new List<PushTarget>();while(rows.Read())result.Add(new(rows.GetString(0),user,Decrypt<PushSubscription>(rows.GetString(1))));return result.ToArray();});
    public void RemovePush(Guid user,string id)=>Run(c=>Execute(c,"DELETE FROM nh_push_subscriptions WHERE user_id=$0 AND id=$1",user,id));
    public PushJob[] PushJobs()=>Run(c=>{using var cmd=Command(c,"SELECT id,user_id,notification_id,event_id,attempts FROM nh_push_jobs WHERE next_at<=$0 ORDER BY next_at LIMIT 20",Now);using var rows=cmd.ExecuteReader();var result=new List<PushJob>();while(rows.Read())result.Add(new(rows.GetString(0),Guid.Parse(rows.GetString(1)),Guid.Parse(rows.GetString(2)),Guid.Parse(rows.GetString(3)),rows.GetInt32(4)));return result.ToArray();});
    public void CompletePush(PushJob job,bool done)=>Run(c=>done||job.Attempts>=2?Execute(c,"DELETE FROM nh_push_jobs WHERE id=$0",job.Id):Execute(c,"UPDATE nh_push_jobs SET attempts=attempts+1,next_at=$1 WHERE id=$0",job.Id,Now+60*(job.Attempts+1)));
}
