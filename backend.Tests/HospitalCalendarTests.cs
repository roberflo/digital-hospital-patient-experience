using Recepcion.Integrations;
using Xunit;
public class HospitalCalendarTests
{
    [Theory]
    [InlineData("2026-10-04T01:00:00Z","2026-10-03")]
    [InlineData("2026-10-04T06:00:00Z","2026-10-04")]
    [InlineData("2026-10-03T19:00:00-06:00","2026-10-03")]
    public void AvailabilityUsesHospitalDayInsteadOfUtcDay(string instant,string expected)
        =>Assert.Equal(DateOnly.Parse(expected),HospitalClient.ClinicalDay(DateTimeOffset.Parse(instant),"America/El_Salvador"));
}
