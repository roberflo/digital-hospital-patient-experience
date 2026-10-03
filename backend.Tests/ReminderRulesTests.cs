using Recepcion;
using Xunit;
public class ReminderRulesTests
{
    [Theory]
    [InlineData("2026-10-05T16:30:00Z","America/El_Salvador","2026-10-04T15:00:00Z")]
    [InlineData("2026-10-05T06:30:00Z","America/El_Salvador","2026-10-04T15:00:00Z")]
    [InlineData("2026-03-08T16:00:00Z","America/New_York","2026-03-07T14:00:00Z")]
    public void DayBeforeUsesNineAmHospitalTime(string starts,string zone,string due)
    {
        Assert.Equal(DateTimeOffset.Parse(due),ReminderRules.Due(DateTimeOffset.Parse(starts),"day_before",zone));
        Assert.Equal(DateTimeOffset.Parse(starts).AddHours(-1),ReminderRules.Due(DateTimeOffset.Parse(starts),"hour_before",zone));
    }
    [Theory][InlineData(" baja ","off")][InlineData("ACTIVAR RECORDATORIOS","on")][InlineData("Quiero cancelar mi cita",null)]
    public void ConsentCommandsDoNotMistakeAppointmentCancellation(string text,string? expected)=>Assert.Equal(expected,ReminderRules.ConsentCommand(text));
}
