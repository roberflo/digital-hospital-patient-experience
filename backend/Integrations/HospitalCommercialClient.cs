using System.Net;
using System.Text.Json;
namespace Recepcion.Integrations;

public sealed partial class HospitalClient
{
    public Task<HospitalCommercialPage<HospitalCompany>> Companies(Guid tenant,string? q,int page,CancellationToken ct=default) => ReadAsync<HospitalCommercialPage<HospitalCompany>>(tenant,$"v1/commercial/companies?q={Uri.EscapeDataString(q??"")}&page={page}&pageSize=100",ct);
    public Task<HospitalCommercialPage<HospitalService>> Services(Guid tenant,string? q,int page,CancellationToken ct=default) => ReadAsync<HospitalCommercialPage<HospitalService>>(tenant,$"v1/commercial/services?active=true&q={Uri.EscapeDataString(q??"")}&page={page}&pageSize=100",ct);
    public Task<HospitalCommercialPage<HospitalCustomer>> Customers(Guid tenant,string phone,CancellationToken ct=default) => ReadAsync<HospitalCommercialPage<HospitalCustomer>>(tenant,$"v1/commercial/customers?phone={Uri.EscapeDataString(phone)}&pageSize=100",ct);
    public Task<HospitalCustomer> Customer(Guid tenant,Guid id,CancellationToken ct=default) => ReadAsync<HospitalCustomer>(tenant,$"v1/commercial/customers/{id}",ct);
    public async Task<HospitalCustomer> LinkCustomerPatient(Guid tenant,Guid patientId,Guid contactId,Guid? customerId=null,int? version=null,CancellationToken ct=default)
    {
        using var res=await SendAsync(tenant,HttpMethod.Post,"v1/commercial/customers",new{patientId,customerId,version,externalReference="recepcion:"+contactId},ct);
        return await ParseAsync<HospitalCustomer>(res,ct);
    }
    public async Task<HospitalQuote> Quote(Guid tenant,HospitalQuoteInput input,CancellationToken ct=default)
    {
        using var res=await SendAsync(tenant,HttpMethod.Post,"v1/commercial/quotes",input,ct);return await ParseAsync<HospitalQuote>(res,ct);
    }
    public async Task<HospitalPurchase> Purchase(Guid tenant,JsonElement body,CancellationToken ct=default)
    {
        using var res=await SendAsync(tenant,HttpMethod.Post,"v1/commercial/purchases",body,ct);return await ParseAsync<HospitalPurchase>(res,ct);
    }
    public Task<HospitalCommercialPage<HospitalPurchase>> Purchases(Guid tenant,Guid customer,int page=1,CancellationToken ct=default) => ReadAsync<HospitalCommercialPage<HospitalPurchase>>(tenant,$"v1/commercial/purchases?customerId={customer}&page={page}&pageSize=25",ct);
    public static void VerifyCustomer(HospitalCustomer customer,Contact contact)
    {
        if(customer.Id==Guid.Empty||NormalizePhone(customer.Phone)!=NormalizePhone(contact.Phone)||NormalizePhone(contact.Phone) is null
            ||customer.PatientId is {} patient&&contact.PatientId is {} linked&&patient!=linked
            ||!string.IsNullOrEmpty(customer.ExternalReference)&&customer.ExternalReference!="recepcion:"+contact.Id)
            throw new HospitalIntegrationException("hospital.customer_identity_mismatch",HttpStatusCode.NotFound);
        if(customer.Source is not ("hospital_patient" or "purchase"))throw new HospitalIntegrationException("hospital.customer_evidence_missing",HttpStatusCode.BadGateway);
    }
}
public sealed record HospitalCommercialPage<T>(IReadOnlyList<T> Items,int Total,int Page,int PageSize);
public sealed record HospitalAgreement(string Name,decimal DiscountPercent,bool Active);
public sealed record HospitalCompany(Guid Id,string Name,string TaxId,string Email,string Phone,bool Active,int Version,HospitalAgreement? Agreement);
public sealed record HospitalService(Guid Id,string Code,string Name,string Kind,decimal Price,string Currency,bool Active,int Version);
public sealed record HospitalCustomer(Guid Id,string Name,string Phone,string Email,Guid? PatientId,string? ExternalReference,Guid? CompanyId,string? CompanyName,string Source,int Version,DateTimeOffset CreatedAt);
public sealed record HospitalQuoteLineInput(Guid ServiceId,int Quantity);
public sealed record HospitalQuoteInput(Guid? CustomerId,Guid? CompanyId,IReadOnlyList<HospitalQuoteLineInput> Lines);
public sealed record HospitalQuoteLine(Guid ServiceId,int ServiceVersion,string Code,string Name,string Kind,int Quantity,decimal UnitPrice,decimal Subtotal,decimal DiscountAmount,decimal Total);
public sealed record HospitalQuote(string QuoteVersion,Guid? CustomerId,Guid? CompanyId,string Currency,decimal DiscountPercent,decimal Subtotal,decimal DiscountAmount,decimal Total,IReadOnlyList<HospitalQuoteLine> Lines);
public sealed record HospitalPurchase(Guid Id,Guid CustomerId,Guid IdempotencyKey,string Status,DateTimeOffset CreatedAt,string? PaymentReference,HospitalQuote Quote);
