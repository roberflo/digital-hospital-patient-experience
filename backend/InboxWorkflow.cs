using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public static class InboxWorkflow
{
    public static readonly string[] States = ["open", "pending", "snoozed", "resolved"];
    public static readonly string[] Priorities = ["low", "normal", "high", "urgent"];
    public static string Labels(string value)
    {
        var labels=value.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Select(x=>x.ToLowerInvariant()).Distinct().ToArray();
        if(labels.Length>10||labels.Any(x=>x.Length>40||x.Any(char.IsControl)))throw new ArgumentException("Usa hasta 10 etiquetas de 40 caracteres");
        return string.Join(", ",labels);
    }
    public static void SetState(Conversation c,string state,DateTimeOffset? until=null)
    {
        if(!States.Contains(state))throw new ArgumentException("Estado de conversación inválido");
        if(state=="snoozed"&&(until is null||until<=DateTimeOffset.UtcNow||until>DateTimeOffset.UtcNow.AddDays(30)))
            throw new ArgumentException("Pospón entre ahora y los próximos 30 días");
        c.State=state;c.SnoozedUntil=state=="snoozed"?until:null;
        if(state=="resolved")c.Status="closed";
        else if(state!="open"||c.Status=="closed")c.Status="human";
        c.Revision++;c.UpdatedAt=DateTimeOffset.UtcNow;
    }
    public static bool ReopenOnInbound(Conversation c)
    {
        if(c.State=="open"&&c.Status!="closed")return false;
        c.State="open";c.Status="human";c.SnoozedUntil=null;return true;
    }
    public static Activity Event(TenantScope scope,Conversation c,string actor,string kind,string body)=>
        new(){TenantId=scope.Id,ContactId=c.ContactId,ConversationId=c.Id,Actor=actor,ActorRole=actor=="Sistema"?"system":actor=="WhatsApp"?"external":"unknown",Kind=kind,Body=body};
    public static Activity Event(TenantScope scope,Conversation c,CurrentUser user,string kind,string body)=>
        new(){TenantId=scope.Id,ContactId=c.ContactId,ConversationId=c.Id,Actor=user.Name,ActorRole=user.Role,ActorSubject=user.Subject,Kind=kind,Body=body};

    public static async Task WakeDue(CrmDb db,TenantScope scope,ConversationService service,CancellationToken ct)
    {
        var ids=await db.Conversations.Where(x=>x.State=="snoozed"&&x.SnoozedUntil<=DateTimeOffset.UtcNow).Select(x=>x.Id).Take(50).ToListAsync(ct);
        foreach(var id in ids){
            using var lease=await service.Lock(id,ct);
            var row=await db.Conversations.SingleAsync(x=>x.Id==id,ct);await db.Entry(row).ReloadAsync(ct);
            if(row.State!="snoozed"||row.SnoozedUntil>DateTimeOffset.UtcNow)continue;
            SetState(row,"open");
            db.Activities.Add(Event(scope,row,"Sistema","conversation_state","Conversación reabierta al vencer la espera."));
            await db.SaveChangesAsync(ct);
        }
    }
}
