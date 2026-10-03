using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public static class InboxEndpoints
{
    public static void MapInbox(this WebApplication app)
    {
        var api=app.MapGroup("/api").RequireAuthorization();
        api.MapPatch("/conversations/{id:guid}/workflow",async(Guid id,WorkflowInput b,CrmDb db,CurrentUser user,TenantScope scope,ConversationService service)=>{
            using var lease=await service.Lock(id);
            var row=await db.Conversations.SingleOrDefaultAsync(x=>x.Id==id);if(row is null)return Results.NotFound();
            if(b.ExpectedRevision is {} revision&&revision!=row.Revision)return Results.Conflict(new{title="La conversación cambió; actualiza antes de guardar"});
            var changes=new List<string>();
            if(b.State is {} state){InboxWorkflow.SetState(row,state,b.SnoozedUntil);changes.Add("Estado: "+state);}
            if(b.Priority is {} priority){if(!InboxWorkflow.Priorities.Contains(priority))throw new ArgumentException("Prioridad inválida");row.Priority=priority;changes.Add("Prioridad: "+priority);}
            if(b.Labels is {} labels){row.Labels=InboxWorkflow.Labels(labels);changes.Add("Etiquetas: "+row.Labels);}
            if(changes.Count==0)throw new ArgumentException("Indica el cambio que quieres guardar");
            row.Revision++;row.UpdatedAt=DateTimeOffset.UtcNow;
            db.Activities.Add(InboxWorkflow.Event(scope,row,user.Name,"conversation_state",string.Join(". ",changes)));
            CrmEndpoints.Audit(db,scope,user,"conversation.workflow",id);await db.SaveChangesAsync();return Results.Ok(row);
        });
        api.MapPost("/conversations/{id:guid}/read",async(Guid id,ReadInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService service)=>{
            using var lease=await service.Lock(id);
            if(!await db.Conversations.AnyAsync(x=>x.Id==id))return Results.NotFound();
            var message=await db.Messages.SingleOrDefaultAsync(x=>x.Id==input.ThroughMessageId&&x.ConversationId==id);
            if(message is null)return Results.NotFound();
            var row=await db.ConversationReads.SingleOrDefaultAsync(x=>x.ConversationId==id&&x.Subject==user.Subject);
            if(row is null){row=new ConversationRead{TenantId=scope.Id,ConversationId=id,Subject=user.Subject};db.Add(row);}
            if(row.LastReadAt<message.ReceivedAt)row.LastReadAt=message.ReceivedAt;
            await db.SaveChangesAsync();return Results.Ok();
        });
        api.MapGet("/contacts/{id:guid}/context",async(Guid id,CrmDb db)=>{
            var contact=await db.Contacts.SingleOrDefaultAsync(x=>x.Id==id);if(contact is null)return Results.NotFound();
            var company=contact.CompanyId is {} cid?await db.Companies.SingleOrDefaultAsync(x=>x.Id==cid):null;
            return Results.Ok(new{contact,company,
                opportunities=await db.Opportunities.Where(x=>x.ContactId==id).OrderByDescending(x=>x.UpdatedAt).Take(100).ToListAsync(),
                activities=await db.Activities.Where(x=>x.ContactId==id).OrderByDescending(x=>x.CreatedAt).Take(100).ToListAsync(),
                conversations=await db.Conversations.Where(x=>x.ContactId==id).OrderByDescending(x=>x.UpdatedAt).Select(x=>new{x.Id,x.State,x.Status,x.ChannelId,x.UpdatedAt}).ToListAsync()});
        });
        api.MapPatch("/contacts/{id:guid}/profile",async(Guid id,ProfileInput b,CrmDb db,TenantScope scope,CurrentUser user)=>{
            var row=await db.Contacts.SingleOrDefaultAsync(x=>x.Id==id);if(row is null)return Results.NotFound();
            if(b.LifecycleStage is not("lead" or "active" or "inactive"))throw new ArgumentException("Estado de cliente inválido");
            if(b.CompanyId is {} company&&!await db.Companies.AnyAsync(x=>x.Id==company))return Results.NotFound();
            row.Name=Rules.Required(b.Name);row.Email=(b.Email??"").Trim();if(row.Email.Length>320)throw new ArgumentException("Correo demasiado largo");
            row.Tags=InboxWorkflow.Labels(b.Tags??"");row.CompanyId=b.CompanyId;row.LifecycleStage=b.LifecycleStage;
            db.Activities.Add(new Activity{TenantId=scope.Id,ContactId=id,Actor=user.Name,Kind="contact_updated",Body="Ficha CRM actualizada: datos de contacto, etiquetas, empresa y estado del cliente."});
            CrmEndpoints.Audit(db,scope,user,"contact.profile",id);await db.SaveChangesAsync();return Results.Ok(row);
        });
        api.MapGet("/saved-replies",async(CrmDb db)=>await db.SavedReplies.OrderBy(x=>x.Title).Take(200).ToListAsync());
        api.MapPost("/saved-replies",async(SavedReplyInput b,CrmDb db,TenantScope scope,CurrentUser user)=>{
            user.RequireSupervisor();var row=new SavedReply{TenantId=scope.Id,Title=Rules.Required(b.Title,80),Body=Rules.Required(b.Body,4000)};
            db.Add(row);CrmEndpoints.Audit(db,scope,user,"saved_reply.created",row.Id);await db.SaveChangesAsync();return Results.Ok(row);
        });
        api.MapDelete("/saved-replies/{id:guid}",async(Guid id,CrmDb db,TenantScope scope,CurrentUser user)=>{
            user.RequireSupervisor();var row=await db.SavedReplies.SingleOrDefaultAsync(x=>x.Id==id);if(row is null)return Results.NotFound();
            db.Remove(row);CrmEndpoints.Audit(db,scope,user,"saved_reply.deleted",id);await db.SaveChangesAsync();return Results.Ok();
        });
    }
}
public record WorkflowInput(string? State,string? Priority,string? Labels,DateTimeOffset? SnoozedUntil,long? ExpectedRevision);
public record ReadInput(Guid ThroughMessageId);
public record ProfileInput(string Name,string? Email,string? Tags,string LifecycleStage,Guid? CompanyId);
public record SavedReplyInput(string Title,string Body);
