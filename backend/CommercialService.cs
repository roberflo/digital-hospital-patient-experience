using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public sealed class CommercialService(CrmDb db,TenantScope scope,HospitalClient hospital,ConversationService locks)
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static void RequireOperator(CurrentUser user){if(user.Role is not("admin" or "agent"))throw new AccessDeniedException();}
    public async Task<HospitalCustomer> Link(Contact contact,Guid customerId,CurrentUser user,CancellationToken ct=default)
    {
        RequireOperator(user);using var lease=await locks.Lock(contact.Id,ct);await db.Entry(contact).ReloadAsync(ct);
        await RequireNoPendingPurchase(contact.Id,ct);
        var customer=await hospital.Customer(scope.Id,customerId,ct);await Bind(contact,customer,user,"hospital_link",ct);await db.SaveChangesAsync(ct);return customer;
    }
    public async Task<HospitalCustomer> LinkPatient(Contact contact,Guid patientId,CurrentUser user,CancellationToken ct=default)
    {
        RequireOperator(user);using var lease=await locks.Lock(contact.Id,ct);await db.Entry(contact).ReloadAsync(ct);
        await RequireNoPendingPurchase(contact.Id,ct);
        if(contact.PatientId is {} existing&&existing!=patientId)throw new ArgumentException("El contacto ya tiene otro expediente. Revisa su vínculo antes de continuar.");
        await hospital.GetVerifiedPatientAsync(scope.Id,patientId,contact.Phone,ct);
        HospitalCustomer? existingCustomer=null;
        if(contact.HospitalCustomerId is {} existingCustomerId){existingCustomer=await hospital.Customer(scope.Id,existingCustomerId,ct);HospitalClient.VerifyCustomer(existingCustomer,contact);}
        var customer=await hospital.LinkCustomerPatient(scope.Id,patientId,contact.Id,contact.HospitalCustomerId,existingCustomer?.Version,ct);
        await Bind(contact,customer,user,"hospital_link",ct);contact.PatientId=patientId;await db.SaveChangesAsync(ct);return customer;
    }
    async Task RequireNoPendingPurchase(Guid contactId,CancellationToken ct)
    {
        if(await db.Opportunities.AnyAsync(o=>o.ContactId==contactId&&o.HospitalPurchaseRequest!=null&&o.HospitalPurchaseId==null,ct))
            throw new ArgumentException("Hay una compra en comprobación para este contacto. Recupérala antes de cambiar el vínculo con Hospital.");
    }
    async Task Bind(Contact contact,HospitalCustomer customer,CurrentUser user,string source,CancellationToken ct)
    {
        HospitalClient.VerifyCustomer(customer,contact);
        if(contact.HospitalCustomerId is {} old&&old!=customer.Id)throw new ArgumentException("Este contacto ya está vinculado a otro cliente Hospital.");
        if(await db.Contacts.AnyAsync(c=>c.HospitalCustomerId==customer.Id&&c.Id!=contact.Id,ct))throw new ArgumentException("Este cliente Hospital ya está vinculado a otro contacto.");
        var first=contact.HospitalCustomerId is null;
        contact.HospitalCustomerId=customer.Id;contact.HospitalCompanyId=customer.CompanyId;contact.HospitalCompanyName=customer.CompanyName;contact.CommercialSyncedAt=DateTimeOffset.UtcNow;
        if(!first)return;
        contact.CustomerSince=DateTimeOffset.UtcNow;contact.CustomerSource=source;contact.LifecycleStage="active";
        db.Activities.Add(new(){TenantId=scope.Id,ContactId=contact.Id,Actor=user.Name,ActorSubject=user.Subject,ActorRole=user.Role,Kind="customer_converted",Body=source=="purchase"?"Contacto convertido en cliente: compra pagada confirmada en Hospital.":"Contacto convertido en cliente: vínculo verificado con un cliente Hospital."});
        CrmEndpoints.Audit(db,scope,user,"customer.converted",contact.Id);
    }
    public async Task<bool> Sync(Contact contact,CurrentUser user,CancellationToken ct=default)
    {
        using var lease=await locks.Lock(contact.Id,ct);await db.Entry(contact).ReloadAsync(ct);
        HospitalCustomer? customer;
        if(contact.HospitalCustomerId is {} customerId)customer=await hospital.Customer(scope.Id,customerId,ct);
        else
        {
            if(await db.Opportunities.AnyAsync(o=>o.ContactId==contact.Id&&o.HospitalPurchaseRequest!=null&&o.HospitalPurchaseId==null,ct))return false;
            var page=await hospital.Customers(scope.Id,contact.Phone,ct);
            // A shared phone never silently chooses one customer. Explicit linking resolves ambiguity.
            if(page.Total!=1||page.Items.Count!=1)return false;
            customer=page.Items[0];HospitalClient.VerifyCustomer(customer,contact);
            if(customer.ExternalReference!="recepcion:"+contact.Id && !(contact.PatientId is {} patient && customer.PatientId==patient))return false;
            var purchases=await hospital.Purchases(scope.Id,customer.Id,ct:ct);
            if(!purchases.Items.Any(p=>p.CustomerId==customer.Id&&p.Status=="completed"))return false;
        }
        await Bind(contact,customer,user,"purchase",ct);await db.SaveChangesAsync(ct);return true;
    }
    public async Task<HospitalQuote> Quote(Opportunity opportunity,Contact contact,IReadOnlyList<HospitalQuoteLineInput> lines,Guid? company,CurrentUser user,CancellationToken ct=default)
    {
        RequireOperator(user);using var lease=await locks.Lock(contact.Id,ct);await db.Entry(opportunity).ReloadAsync(ct);await db.Entry(contact).ReloadAsync(ct);
        if(opportunity.HospitalPurchaseId is not null||opportunity.HospitalPurchaseRequest is not null)throw new ArgumentException("Esta oportunidad tiene una compra registrada o en comprobación. Resuelve la compra antes de cambiar la cotización.");
        if(contact.HospitalCustomerId is {} cid){var customer=await hospital.Customer(scope.Id,cid,ct);HospitalClient.VerifyCustomer(customer,contact);company=customer.CompanyId;}
        var quote=await hospital.Quote(scope.Id,new(contact.HospitalCustomerId,company,lines),ct);
        opportunity.HospitalQuote=JsonSerializer.Serialize(quote,Json);opportunity.Value=quote.Total;opportunity.UpdatedAt=DateTimeOffset.UtcNow;
        CrmEndpoints.Audit(db,scope,user,"opportunity.quoted",opportunity.Id);await db.SaveChangesAsync(ct);return quote;
    }
    public async Task<HospitalPurchase> Purchase(Opportunity opportunity,Contact contact,PurchaseConfirmation input,CurrentUser user,CancellationToken ct=default)
    {
        RequireOperator(user);if(!input.PaymentReceived)throw new ArgumentException("Confirma que el pago ya fue recibido. Esta acción sólo registra la compra.");
        using var lease=await locks.Lock(contact.Id,ct);await db.Entry(opportunity).ReloadAsync(ct);await db.Entry(contact).ReloadAsync(ct);
        var quote=JsonSerializer.Deserialize<HospitalQuote>(opportunity.HospitalQuote??"null",Json)??throw new ArgumentException("Cotiza los servicios antes de registrar la compra.");
        if(input.QuoteVersion!=quote.QuoteVersion)throw new ArgumentException("La cotización cambió. Revisa los importes antes de registrar la compra.");
        var reference=(input.PaymentReference??"").Trim();if(reference.Length>120)throw new ArgumentException("Referencia de pago demasiado larga.");
        if(opportunity.HospitalPurchaseRequest is null)
        {
            if(quote.CustomerId!=contact.HospitalCustomerId)throw new ArgumentException("El vínculo del cliente cambió. Vuelve a cotizar antes de registrar la compra.");
            if(contact.HospitalCustomerId is {} currentId)HospitalClient.VerifyCustomer(await hospital.Customer(scope.Id,currentId,ct),contact);
            opportunity.HospitalPurchaseRequest=JsonSerializer.Serialize(new{customerId=quote.CustomerId,companyId=quote.CompanyId,lines=quote.Lines.Select(l=>new{l.ServiceId,l.Quantity}),quoteVersion=quote.QuoteVersion,idempotencyKey=opportunity.Id,paymentReference=reference,
                customer=quote.CustomerId is null?new{name=contact.Name,phone=contact.Phone,email=contact.Email,externalReference="recepcion:"+contact.Id}:null},Json);
            await db.SaveChangesAsync(ct); // Preserve the exact request across an unknown network result.
        }
        var payload=JsonSerializer.Deserialize<JsonElement>(opportunity.HospitalPurchaseRequest,Json);
        if(payload.GetProperty("paymentReference").GetString()!=reference)throw new ArgumentException("Hay una compra en comprobación. Reintenta con la misma referencia de pago.");
        HospitalPurchase purchase;
        try {purchase=await hospital.Purchase(scope.Id,payload,ct);}
        catch(HospitalIntegrationException ex) when(ex.Code is "commercial.quote_changed" or "commercial.invalid_purchase" or "commercial.invalid_input" or "commercial.invalid_lines" or "commercial.invalid_phone" or "commercial.not_found" or "commercial.company_unavailable" or "commercial.service_unavailable" or "commercial.customer_company_conflict" or "commercial.customer_link_conflict")
        {opportunity.HospitalPurchaseRequest=null;await db.SaveChangesAsync(CancellationToken.None);throw;}
        if(purchase.Status!="completed"||purchase.Id==Guid.Empty||purchase.IdempotencyKey!=opportunity.Id||purchase.Quote.QuoteVersion!=quote.QuoteVersion)
            throw new HospitalIntegrationException("hospital.purchase_unconfirmed",HttpStatusCode.BadGateway);
        var customer=await hospital.Customer(scope.Id,purchase.CustomerId,ct);await Bind(contact,customer,user,"purchase",ct);
        if(opportunity.HospitalPurchaseId is null)
        {
            opportunity.HospitalPurchaseId=purchase.Id;opportunity.Stage="won";opportunity.Value=purchase.Quote.Total;opportunity.UpdatedAt=DateTimeOffset.UtcNow;
            db.Activities.Add(new(){TenantId=scope.Id,ContactId=contact.Id,ConversationId=opportunity.ConversationId,Actor=user.Name,ActorRole=user.Role,ActorSubject=user.Subject,Kind="purchase",Body="Compra pagada registrada en Hospital: "+opportunity.Title});
            CrmEndpoints.Audit(db,scope,user,"opportunity.purchased",opportunity.Id);
        }
        await db.SaveChangesAsync(ct);return purchase;
    }
}
public sealed record PurchaseConfirmation(string QuoteVersion,bool PaymentReceived,string? PaymentReference);
