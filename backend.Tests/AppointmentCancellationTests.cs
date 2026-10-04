using Recepcion.Integrations;
using Xunit;

// Hospital distinguishes who cancelled an appointment. Recepción used to send
// "patient-requested / cancelledByPatient = true" for every cancellation, so a
// cancellation made by staff was recorded — and shown — as «Cancelada por paciente».
public sealed class AppointmentCancellationTests
{
    [Theory]
    [InlineData("patient-requested", true)]
    [InlineData("clinician-unavailable", false)]
    [InlineData("clinic-closed", false)]
    [InlineData("duplicate", false)]
    [InlineData("other", false)]
    public void OnlyAPatientRequestIsAttributedToThePatient(string reason, bool byPatient)
    {
        var cancellation = AppointmentCancellation.From(reason);
        Assert.Equal(reason, cancellation.Reason);
        Assert.Equal(byPatient, cancellation.CancelledByPatient);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("because")]
    public void AnUnstatedOrUnknownReasonIsRefused(string? reason) =>
        Assert.Throws<ArgumentException>(() => AppointmentCancellation.From(reason));
}
