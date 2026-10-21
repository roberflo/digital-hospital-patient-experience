using Recepcion;
using Xunit;
public sealed partial class AgentIntegrationTests {
    // Con el agente apagado, un toque en «Cambiar fecha» o «Cancelar» sólo deja la nota: Mañana lo muestra aparte, no como «Sin respuesta».
    [Fact] public async Task TomorrowMarksWhoAskedToChangeSinceTheReminderWent()
    {
        var now=new DateTimeOffset(2026,10,3,15,0,0,TimeSpan.Zero);
        contact.PatientId=Guid.NewGuid();
        Message Sent()=>new(){TenantId=scope.Id,ConversationId=conversation.Id,Sender="system",Type="template",Status="read",Body="Recordatorio sintético"};
        AppointmentReminder Row(string start,Message sent)=>new(){TenantId=scope.Id,ContactId=contact.Id,PatientId=contact.PatientId.Value,ChannelId=conversation.ChannelId,AppointmentId=Guid.NewGuid(),StartsAt=DateTimeOffset.Parse(start),DueAt=now,Window="day_before",Status="sent",AttemptedAt=now.AddHours(-2),MessageId=sent.Id};
        var first=Sent();var second=Sent();db.AddRange(first,second);
        var asked=Row("2026-10-04T15:00:00Z",first);var other=Row("2026-10-04T17:00:00Z",second);
        db.AddRange(asked,other);
        Activity Request()=>new(){TenantId=scope.Id,ContactId=contact.Id,ConversationId=conversation.Id,MessageId=first.Id,Kind="reminder_change_request",Actor="Recordatorios",ActorRole="system",Body="El paciente pidió cambiar la cita desde el recordatorio.",CreatedAt=now.AddHours(-1)};
        db.Add(Request());await db.SaveChangesAsync();
        var staff=new CurrentUser{Role="agent",Subject="agent",Name="agent"};
        var list=await ReminderEndpoints.Tomorrow(db,staff,scope,now);
        // Dos citas mañana, el toque fue sobre el recordatorio de la primera: sólo ésa pidió cambio.
        Assert.True(list.Single(r=>r.Id==asked.Id).ChangeRequested);Assert.False(list.Single(r=>r.Id==other.Id).ChangeRequested);
        // Recepción respondió después: la petición quedó atendida.
        db.Add(new Message{TenantId=scope.Id,ConversationId=conversation.Id,Sender="human",Body="Le llamamos para cambiarla.",Status="sent",RequestKey="answer-"+Guid.NewGuid(),CreatedAt=now.AddMinutes(-30)});await db.SaveChangesAsync();
        Assert.False((await ReminderEndpoints.Tomorrow(db,staff,scope,now)).Single(r=>r.Id==asked.Id).ChangeRequested);
    }
}
