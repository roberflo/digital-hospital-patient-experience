using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Recepcion.Integrations;

var tenant = Guid.NewGuid();
var patient = Guid.NewGuid();
var otherPatient = Guid.NewGuid();
var appointment = Guid.NewGuid();
var clinician = Guid.NewGuid();
var prescription = Guid.NewGuid();
var calls = new List<string>();
var data = new Dictionary<string, string?>
{
    [$"Hospital:Tenants:{tenant}:BaseUrl"] = "https://hospital.example/",
    [$"Hospital:Tenants:{tenant}:AccessToken"] = Token(tenant)
};
var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
var handler = new FakeHandler(request =>
{
    calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
    if (request.RequestUri.AbsolutePath == $"/v1/patients/{patient}")
        return Ok(new { patientId = patient, givenNames = "Paciente", familyNames = "Sintético", phone = "+503 7000-0001" });
    if (request.RequestUri.AbsolutePath == $"/v1/agenda/{appointment}")
        return Ok(new { appointmentId = appointment, patientId = otherPatient, visitKind = "follow-up", status = "booked" });
    if (request.RequestUri.AbsolutePath == $"/v1/reception/prescriptions/{prescription}")
        return Ok(new { prescriptionId = prescription, patientId = otherPatient, encounterId = Guid.NewGuid(), state = "signed", signedAt = DateTimeOffset.UtcNow, lines = Array.Empty<object>() });
    return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("sensitive upstream content") };
});
var client = new HospitalClient(new HttpClient(handler), config);

await client.GetVerifiedPatientAsync(tenant, patient, "50370000001");
await ExpectCode("hospital.patient_phone_mismatch", () => client.GetVerifiedPatientAsync(tenant, patient, "50370000002"));
await ExpectCode("hospital.patient_phone_mismatch", () => client.GetVerifiedPatientAsync(tenant, patient, "70000001"));
await ExpectCode("hospital.patient_phone_mismatch", () => client.GetVerifiedPatientAsync(tenant, patient, ""));
Assert(HospitalClient.NormalizePhone("+503 7000-0001") == "50370000001", "Phone normalization");
Assert(HospitalClient.NormalizePhone("000000000") is null, "Invalid phone rejected");

await ExpectCode("hospital.appointment_unavailable", () => client.CancelAppointmentAsync(tenant, patient, "50370000001", appointment));
Assert(calls.All(x => !x.StartsWith("POST")), "Cross-patient appointment never mutates");
var before = calls.Count;
await ExpectCode("hospital.clinical_delivery_not_authorized", () => client.GetIssuedPrescriptionAsync(tenant, patient, "50370000001", prescription));
Assert(calls.Count == before, "Clinical gate refuses before any network request");

config[$"Hospital:Tenants:{tenant}:AllowClinicalDelivery"] = "true";
await ExpectCode("hospital.clinical_delivery_not_authorized", () => client.GetIssuedPrescriptionAsync(tenant, patient, "50370000001", prescription));
config[$"Hospital:Tenants:{tenant}:UseReceptionBridge"] = "true";
await ExpectCode("hospital.prescription_unavailable", () => client.GetIssuedPrescriptionAsync(tenant, patient, "50370000001", prescription));
Assert(calls.All(x => !x.Contains("print-jobs")), "Cross-patient prescription never prints");

config[$"Hospital:Tenants:{tenant}:AccessToken"] = Token(Guid.NewGuid());
before = calls.Count;
await ExpectCode("hospital.token_tenant_or_expiry_invalid", () => client.GetVerifiedPatientAsync(tenant, patient, "50370000001"));
Assert(calls.Count == before, "Wrong-tenant token never leaves process");
config[$"Hospital:Tenants:{tenant}:AccessToken"] = Token(tenant, -100);
await ExpectCode("hospital.token_tenant_or_expiry_invalid", () => client.GetVerifiedPatientAsync(tenant, patient, "50370000001"));
config[$"Hospital:Tenants:{tenant}:AccessToken"] = "not-a-jwt";
await ExpectCode("hospital.token_tenant_or_expiry_invalid", () => client.GetVerifiedPatientAsync(tenant, patient, "50370000001"));
config[$"Hospital:Tenants:{tenant}:AccessToken"] = Token(tenant);
await ExpectCode("hospital.http_403", () => client.GetAgendaDayAsync(tenant, DateOnly.FromDateTime(DateTime.UtcNow)));

// Exact route and request-body contract on a real successful write.
var createHandler = new FakeHandler(request =>
{
    if (request.Method == HttpMethod.Get)
        return Ok(new { patientId = patient, givenNames = "Paciente", familyNames = "Sintético", phone = "+50370000001" });
    Assert(request.RequestUri!.AbsolutePath == "/v1/agenda", "Create route");
    var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
    Assert(body.RootElement.GetProperty("patientId").GetGuid() == patient, "Create patient");
    Assert(body.RootElement.GetProperty("clinicianId").GetGuid() == clinician, "Create clinician");
    Assert(body.RootElement.GetProperty("visitKind").GetString() == "follow-up", "Wire visit kind");
    return Ok(new { appointmentId = appointment, status = "booked", overlaps = true });
});
var created = await new HospitalClient(new HttpClient(createHandler), config).CreateAppointmentAsync(tenant,
    "50370000001", new(patient, clinician, DateTimeOffset.UtcNow.AddDays(1), 30, "follow-up"));
Assert(created.Overlaps && created.AppointmentId == appointment, "Overlap signal preserved");
Console.WriteLine("PASS: hospital client phone linkage, tenant/expiry guard, cross-patient appointment and prescription isolation, clinical gate, error redaction, exact booking contract and overlap signal.");

static string Token(Guid tenant, int expiresIn = 3600) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
    JsonSerializer.Serialize(new { tenant_id = tenant, exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn })))
    .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test-signature";
static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
static void Assert(bool value, string name) { if (!value) throw new Exception($"FAILED: {name}"); }
static async Task ExpectCode(string code, Func<Task> action)
{
    try { await action(); }
    catch (HospitalIntegrationException e) when (e.Code == code) { return; }
    throw new Exception($"FAILED: expected {code}");
}
sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
}
