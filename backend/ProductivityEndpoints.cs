using Microsoft.EntityFrameworkCore;
namespace Recepcion;

public record ViewFilters(string State="",string Assignment="all",string ChannelId="",string Priority="",string Label="",string Mode="all");
public record ViewInput(string Name,ViewFilters Filters);
public record MacroInput(string Name,string? State,string? Priority,string? Labels,string? Note,bool TakeOwnership=false);
public record ApplyMacroInput(long ExpectedRevision);

public static class ProductivityEndpoints
{
    public static void MapProductivity(this WebApplication app)
    {
        var api=app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/inbox-views",async(CrmDb db,CurrentUser user)=>await db.InboxViews.Where(x=>x.Subject==user.Subject).OrderBy(x=>x.Name).ToListAsync());
        api.MapPost("/inbox-views",async(ViewInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService locks)=>{
            using var lease=await locks.Lock(scope.Id);
            if(await db.InboxViews.CountAsync(x=>x.Subject==user.Subject)>=20)throw new ArgumentException("Puedes guardar hasta 20 vistas personales");
            var filters=ValidateFilters(input.Filters);
            if(Guid.TryParse(filters.ChannelId,out var channel)&&!await db.Channels.AnyAsync(x=>x.Id==channel))return Results.NotFound();
            var row=new InboxView{TenantId=scope.Id,Subject=user.Subject,Name=Rules.Required(input.Name,60),Filters=System.Text.Json.JsonSerializer.Serialize(filters,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))};
            db.Add(row);await db.SaveChangesAsync();return Results.Ok(row);
        });
        api.MapDelete("/inbox-views/{id:guid}",async(Guid id,CrmDb db,CurrentUser user)=>{
            var row=await db.InboxViews.SingleOrDefaultAsync(x=>x.Id==id&&x.Subject==user.Subject);if(row is null)return Results.NotFound();
            db.Remove(row);await db.SaveChangesAsync();return Results.Ok();
        });
        api.MapGet("/macros",async(CrmDb db)=>await db.Macros.OrderBy(x=>x.Name).Take(100).ToListAsync());
        api.MapPost("/macros",async(MacroInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService locks)=>{
            user.RequireAdmin();using var lease=await locks.Lock(scope.Id);
            if(await db.Macros.CountAsync()>=100)throw new ArgumentException("Puedes guardar hasta 100 macros por hospital");
            var row=CreateMacro(input);row.TenantId=scope.Id;
            db.Add(row);CrmEndpoints.Audit(db,scope,user,"macro.created",row.Id);await db.SaveChangesAsync();return Results.Ok(row);
        });
        api.MapDelete("/macros/{id:guid}",async(Guid id,CrmDb db,CurrentUser user,TenantScope scope)=>{
            user.RequireAdmin();var row=await db.Macros.SingleOrDefaultAsync(x=>x.Id==id);if(row is null)return Results.NotFound();
            db.Remove(row);CrmEndpoints.Audit(db,scope,user,"macro.deleted",id);await db.SaveChangesAsync();return Results.Ok();
        });
        api.MapPost("/conversations/{id:guid}/macros/{macroId:guid}",async(Guid id,Guid macroId,ApplyMacroInput input,CrmDb db,CurrentUser user,TenantScope scope,ConversationService locks)=>{
            using var teamLease=await locks.Lock(scope.Id);using var lease=await locks.Lock(id);
            var row=await db.Conversations.SingleOrDefaultAsync(x=>x.Id==id);
            var macro=await db.Macros.SingleOrDefaultAsync(x=>x.Id==macroId);
            if(row is null||macro is null)return Results.NotFound();
            if(row.Revision!=input.ExpectedRevision)return Results.Conflict(new{title="La conversación cambió. Revisa los cambios antes de aplicar la macro."});
            TeamEndpoints.RequireEditable(row,user);
            Apply(macro,row,user.Subject);
            db.Activities.Add(InboxWorkflow.Event(scope,row,user,"macro","Macro aplicada: "+macro.Name));
            if(!string.IsNullOrWhiteSpace(macro.Note))db.Activities.Add(InboxWorkflow.Event(scope,row,user,"note",macro.Note));
            CrmEndpoints.Audit(db,scope,user,"conversation.macro",id);await db.SaveChangesAsync();return Results.Ok(row);
        });
    }
    public static ViewFilters ValidateFilters(ViewFilters filters)
    {
        if(filters is null||filters.State is null||filters.Assignment is null||filters.ChannelId is null||filters.Priority is null||filters.Label is null||filters.Mode is null)throw new ArgumentException("Filtros requeridos");
        if(filters.State!=""&&!InboxWorkflow.States.Contains(filters.State)||(!new[]{"all","mine","unassigned"}.Contains(filters.Assignment)&&!(filters.Assignment.StartsWith("member:",StringComparison.Ordinal)&&filters.Assignment.Length is >7 and <=207))||filters.Priority!=""&&!InboxWorkflow.Priorities.Contains(filters.Priority)||!new[]{"all","human","agent"}.Contains(filters.Mode))throw new ArgumentException("Filtro inválido");
        if(filters.ChannelId!=""&&!Guid.TryParse(filters.ChannelId,out _))throw new ArgumentException("Canal inválido");
        if(filters.Label.Length>40||filters.Label.Contains(','))throw new ArgumentException("Usa una sola etiqueta por vista");
        return filters with{Label=InboxWorkflow.Labels(filters.Label)};
    }
    public static ConversationMacro CreateMacro(MacroInput input)
    {
        if(input.State is not(null or "" or "open" or "pending" or "resolved"))throw new ArgumentException("Estado de macro inválido");
        if(!string.IsNullOrEmpty(input.Priority)&&!InboxWorkflow.Priorities.Contains(input.Priority))throw new ArgumentException("Prioridad inválida");
        var row=new ConversationMacro{Name=Rules.Required(input.Name,80),State=string.IsNullOrEmpty(input.State)?null:input.State,Priority=string.IsNullOrEmpty(input.Priority)?null:input.Priority,Labels=InboxWorkflow.Labels(input.Labels??""),Note=string.IsNullOrWhiteSpace(input.Note)?"":Rules.Required(input.Note,4000),TakeOwnership=input.TakeOwnership};
        if(row.State is null&&row.Priority is null&&row.Labels==""&&row.Note==""&&!row.TakeOwnership)throw new ArgumentException("Agrega al menos una acción a la macro");
        return row;
    }
    public static void Apply(ConversationMacro macro,Conversation row,string subject)
    {
        // Validate before changing the tracked entity so the batch is all-or-nothing.
        var labels=InboxWorkflow.Labels(row.Labels+","+macro.Labels);
        if(macro.State is {} state)InboxWorkflow.SetState(row,state);
        if(macro.Priority is {} priority)row.Priority=priority;
        row.Labels=labels;
        if(macro.TakeOwnership){row.AssignedTo=subject;if(row.State!="resolved")row.Status="human";}
        row.Revision++;row.UpdatedAt=DateTimeOffset.UtcNow;
    }
}
