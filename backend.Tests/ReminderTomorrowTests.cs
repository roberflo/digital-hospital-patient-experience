using Recepcion;
using Xunit;
public sealed partial class AgentIntegrationTests {
    // Con el agente apagado, un toque en «Cambiar fecha» o «Cancelar» sólo deja la nota: Mañana lo muestra aparte, no como «Sin respuesta».
    [Fact] public async Task TomorrowMarksWhoAskedToChangeSinceTheReminderWent()
    {
        var now=new DateTimeOffset(2026,10,3,15,0,0,TimeSpan.Zero);
        contact.PatientId=Guid.NewGuid();
        AppointmentReminder Row(string start,DateTimeOffset attempted)=>new(){TenantId=scope.Id,ContactId=contact.Id,PatientId=contact.PatientId.Value,ChannelId=conversation.ChannelId,AppointmentId=Guid.NewGuid(),StartsAt=DateTimeOffset.Parse(start),DueAt=now,Window="day_before",Status="sent",AttemptedAt=attempted};
        var asked=Row("2026-10-04T15:00:00Z",now.AddHours(-2));var later=Row("2026-10-04T17:00:00Z",now.AddMinutes(-5));
        db.AddRange(asked,later);
        db.Add(new Activity{TenantId=scope.Id,ContactId=contact.Id,ConversationId=conversation.Id,Kind="reminder_change_request",Actor="Recordatorios",ActorRole="system",Body="El paciente pidió cambiar la cita desde el recordatorio.",CreatedAt=now.AddHours(-1)});
        await db.SaveChangesAsync();
        var list=await ReminderEndpoints.Tomorrow(db,new CurrentUser{Role="agent",Subject="agent",Name="agent"},scope,now);
        // La petición es anterior al segundo envío: no se le atribuye.
        Assert.True(list.Single(r=>r.Id==asked.Id).ChangeRequested);Assert.False(list.Single(r=>r.Id==later.Id).ChangeRequested);
    }
}
