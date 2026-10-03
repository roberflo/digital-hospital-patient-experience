using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;
public static class CommercialEndpoints
{
    public static void MapCommercial(this WebApplication app)
    {
        var api=app.MapGroup("/api/commercial").RequireAuthorization();
        api.MapGet("/settings",(HospitalClient h,TenantScope t)=>{
            var value=h.PublicUrl(t.Id);
            return new{hospitalUrl=Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"?uri.GetLeftPart(UriPartial.Authority)+"/es/commercial":null};
        });
        api.MapGet("/companies",async(string? q,int? page,HospitalClient h,TenantScope t)=>await h.Companies(t.Id,Query(q),Page(page)));
        api.MapGet("/services",async(string? q,int? page,HospitalClient h,TenantScope t)=>await h.Services(t.Id,Query(q),Page(page)));
        api.MapGet("/contacts/{id:guid}",async(Guid id,int? page,CrmDb db,TenantScope t,HospitalClient h)=>{
            var contact=await db.Contacts.SingleOrDefaultAsync(c=>c.Id==id);if(contact is null)return Results.NotFound();
            var candidates=await h.Customers(t.Id,contact.Phone);
            HospitalCustomer? customer=null;HospitalCommercialPage<HospitalPurchase>? purchases=null;
            if(contact.HospitalCustomerId is {} cid){customer=await h.Customer(t.Id,cid);HospitalClient.VerifyCustomer(customer,contact);purchases=await h.Purchases(t.Id,cid,Page(page));}
            return Results.Ok(new{contact,customer,candidates=candidates.Items.Where(c=>HospitalClient.NormalizePhone(c.Phone)==contact.Phone&&(string.IsNullOrEmpty(c.ExternalReference)||c.ExternalReference=="recepcion:"+contact.Id)),purchases});
        });
        api.MapPost("/contacts/{id:guid}/link",async(Guid id,CustomerLink b,CrmDb db,CurrentUser u,CommercialService commercial)=>{
            var contact=await db.Contacts.SingleOrDefaultAsync(c=>c.Id==id);if(contact is null)return Results.NotFound();
            var customer=await commercial.Link(contact,b.CustomerId,u);return Results.Ok(new{contact,customer});
        });
        api.MapPost("/contacts/{id:guid}/sync",async(Guid id,CrmDb db,CurrentUser u,CommercialService commercial)=>{
            var contact=await db.Contacts.SingleOrDefaultAsync(c=>c.Id==id);if(contact is null)return Results.NotFound();
            CommercialService.RequireOperator(u);await commercial.Sync(contact,u);return Results.Ok(contact);
        });
        api.MapPost("/opportunities/{id:guid}/quote",async(Guid id,QuoteInput b,CrmDb db,CurrentUser u,CommercialService commercial)=>{
            var opportunity=await db.Opportunities.SingleOrDefaultAsync(o=>o.Id==id);if(opportunity is null)return Results.NotFound();
            var contact=await db.Contacts.SingleAsync(c=>c.Id==opportunity.ContactId);
            return Results.Ok(await commercial.Quote(opportunity,contact,b.Lines,b.CompanyId,u));
        });
        api.MapPost("/opportunities/{id:guid}/purchase",async(Guid id,PurchaseConfirmation b,CrmDb db,CurrentUser u,CommercialService commercial)=>{
            var opportunity=await db.Opportunities.SingleOrDefaultAsync(o=>o.Id==id);if(opportunity is null)return Results.NotFound();
            var contact=await db.Contacts.SingleAsync(c=>c.Id==opportunity.ContactId);
            return Results.Ok(await commercial.Purchase(opportunity,contact,b,u));
        });
    }
    static string? Query(string? q){if(q?.Length>100)throw new ArgumentException("Búsqueda demasiado larga");return q;}
    static int Page(int? page)=>Math.Clamp(page??1,1,10000);
}
public sealed record CustomerLink(Guid CustomerId);
public sealed record QuoteInput(IReadOnlyList<HospitalQuoteLineInput> Lines,Guid? CompanyId);

public sealed class CommercialSyncWorker(IServiceScopeFactory factory,ILogger<CommercialSyncWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var root=factory.CreateScope();var db=root.ServiceProvider.GetRequiredService<CrmDb>();var hospital=root.ServiceProvider.GetRequiredService<HospitalClient>();
                var tenants=await db.Tenants.Select(t=>t.Id).ToListAsync(ct);
                foreach(var tenant in tenants.Where(hospital.IsConfigured))
                {
                    // ponytail: bounded oldest-first batches; replace with Hospital purchase events at high volume.
                    using var batch=factory.CreateScope();batch.ServiceProvider.GetRequiredService<TenantScope>().Id=tenant;
                    var ids=await batch.ServiceProvider.GetRequiredService<CrmDb>().Contacts.OrderBy(c=>c.CommercialSyncedAt).ThenBy(c=>c.Id).Select(c=>c.Id).Take(100).ToListAsync(ct);
                    foreach(var id in ids)
                    {
                        using var s=factory.CreateScope();s.ServiceProvider.GetRequiredService<TenantScope>().Id=tenant;var scoped=s.ServiceProvider.GetRequiredService<CrmDb>();
                        var contact=await scoped.Contacts.SingleAsync(c=>c.Id==id,ct);
                        try{await s.ServiceProvider.GetRequiredService<CommercialService>().Sync(contact,new CurrentUser{Subject="hospital-commercial-sync",Name="Hospital",Role="system"},ct);}
                        catch(Exception ex) when(!ct.IsCancellationRequested&&ex is HospitalIntegrationException or HttpRequestException or ArgumentException or DbUpdateException)
                        {log.LogWarning("Commercial sync unavailable {TenantId} {ErrorType}",tenant,ex.GetType().Name);}
                        await scoped.Contacts.Where(c=>c.Id==id).ExecuteUpdateAsync(set=>set.SetProperty(c=>c.CommercialSyncedAt,DateTimeOffset.UtcNow),ct);
                    }
                }
            }
            catch(Exception ex) when(!ct.IsCancellationRequested){log.LogWarning("Commercial sync unavailable {ErrorType}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromMinutes(5),ct);
        }
    }
}
