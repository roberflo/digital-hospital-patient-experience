using System.Net;

namespace Recepcion.Integrations;

public sealed partial class HospitalClient
{
    /// <summary>Registers an adult through Hospital's own <c>POST v1/patients</c>, so its
    /// validation, duplicate detection and audit apply. Never forces past a duplicate: a
    /// candidate comes back as <c>Created = false</c> and a person decides.</summary>
    public async Task<HospitalRegistration> RegisterPatientAsync(Guid tenantId, HospitalPatientRegistration input, CancellationToken ct = default)
    {
        if (NormalizePhone(input.Phone) is null) throw new HospitalIntegrationException("hospital.patient_phone_mismatch", HttpStatusCode.BadRequest);
        // Hospital has no idempotency contract for registration: never retry this POST after a timeout.
        using var response = await SendAsync(tenantId, HttpMethod.Post, "v1/patients", new
        {
            input.GivenNames, input.FamilyNames, birthDate = input.BirthDate.ToString("yyyy-MM-dd"), input.Sex,
            emergencyContacts = new[] { new { fullName = input.EmergencyName, relationship = input.EmergencyRelationship, phone = input.EmergencyPhone } },
            forceCreateDespiteDuplicate = false, input.Phone
        }, ct);
        return await ParseAsync<HospitalRegistration>(response, ct);
    }
}
public sealed record HospitalPatientRegistration(string GivenNames, string FamilyNames, DateOnly BirthDate, string Sex, string Phone,
    string EmergencyName, string EmergencyRelationship, string EmergencyPhone)
{
    public override string ToString() => "HospitalPatientRegistration";
}
public sealed record HospitalRegistration(bool Created, Guid? PatientId);
