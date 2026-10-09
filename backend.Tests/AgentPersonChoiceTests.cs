using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/reception-agent.md, criterios 44–46: outside an emergency the patient decides whether to go to a person,
/// with a button. The agent explains and offers; it does not take the conversation away on its own.</summary>
public sealed class AgentPersonChoiceTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    List<string> Buttons() => h.Interactive[^1].GetProperty("action").GetProperty("buttons").EnumerateArray().Select(b => b.GetProperty("reply").GetProperty("id").GetString()!).ToList();
    Task<List<Activity>> Trail() => h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).OrderBy(a => a.CreatedAt).ToListAsync();

    [Fact]
    public async Task AgentThatWantsToHandOffAsksFirst()
    {
        var model = h.Model(AgentHarness.ToolCall("handoff", new { reason = "Pregunta por un cambio de dosis" }), AgentHarness.Reply("Te paso con el equipo."));

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status); // still the patient's conversation with the agent
        var sent = Assert.Single(h.Sent);
        Assert.Contains("¿Quieres que te pase con una persona?", sent);
        Assert.DoesNotContain("dosis", sent); // the internal reason is for the team, not for the chat
        Assert.Equal(["PERSONA", "MENU"], Buttons());
        Assert.DoesNotContain(await Trail(), a => a.Kind == "handoff");
    }

    [Theory]
    [InlineData("PERSONA")]
    [InlineData("Sí")]
    [InlineData("si por favor")]
    public async Task ChoosingAPersonHandsOffWithTheReasonTheTeamNeeds(string answer)
    {
        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Pregunta por un cambio de dosis" }), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);
        await h.Say("patient", answer);

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        var conversation = await h.Fresh();
        Assert.Equal("human", conversation.Status);
        Assert.Equal("Pregunta por un cambio de dosis", (await Trail()).Last(a => a.Kind == "handoff").Body); // why the agent offered it
        Assert.StartsWith("Le pasé tu consulta al equipo del hospital", h.Sent[^1]);
    }

    [Fact]
    public async Task ChoosingToStayShowsTheMenu()
    {
        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Consulta de precio" }), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);
        await h.Say("patient", "MENU");

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Contains(AgentHarness.Options(h.Interactive[^1]), option => option.Id == "AGENDAR");
    }

    [Fact]
    public async Task WithNoEmergencyNumberSetUpSymptomsGetTheAppointmentAndNoEmergencyQuestion()
    {
        // The hospital configured no emergency number: there is nowhere to send the patient, so nothing is asked.
        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Dolor de pecho", urgent = true }), AgentHarness.Reply("Siento que te sientas así. Puedo buscarte una cita.\n\n¿Es una emergencia?")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Equal("Siento que te sientas así. Puedo buscarte una cita.", Assert.Single(h.Sent));
        Assert.Empty(h.Interactive);
    }

    [Fact]
    public async Task SymptomsAreAttendedAndThePatientIsAskedWhetherItIsAnEmergency()
    {
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.EmergencyPhone = "2200 0000"; await h.Db.SaveChangesAsync();
        // The model takes symptoms for an emergency. That is the patient's call: the agent stays and asks.
        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Dolor de pecho", urgent = true }), AgentHarness.Reply("Siento que te sientas así. Puedo buscarte una cita.")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        var sent = Assert.Single(h.Sent);
        Assert.StartsWith("Siento que te sientas así", sent);
        Assert.EndsWith("¿Es una emergencia?", sent);
        Assert.Equal(["EMERGENCIA", "no"], Buttons());
        Assert.DoesNotContain(await Trail(), a => a.Kind == "handoff");
    }

    [Fact]
    public async Task UrgentHandoffIsImmediateOnceThePatientWasAsked()
    {
        await h.Say("agent", "Siento que te sientas así.\n\n¿Es una emergencia?"); await h.Say("patient", "Me duele muchísimo, no aguanto");

        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Dolor de pecho", urgent = true }), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.StartsWith("Si es una emergencia", h.Sent[^1]);
    }

    [Theory]
    [InlineData("Quiero hablar con una persona, por favor", new[] { "50370000001", "50370000002" })] // nobody named: the doctors patients may write to
    [InlineData("Quiero hablar con la doctora Rivas", new[] { "50370000001" })]                       // the one the patient named
    [InlineData("PERSONA", new[] { "50370000001", "50370000002" })]
    public async Task AskingForAPersonAlsoOffersTheDoctorsPatientsMayWriteTo(string message, string[] offered)
    {
        h.Db.Members.AddRange(
            new Member { TenantId = h.Scope.Id, Subject = "rivas-" + Guid.NewGuid(), Name = "Dra. Modelo Rivas", Role = "doctor", WhatsAppPhone = "+503 7000 0001" },
            new Member { TenantId = h.Scope.Id, Subject = "mejia-" + Guid.NewGuid(), Name = "Dr. Prueba Mejía", Role = "doctor", WhatsAppPhone = "+503 7000 0002" },
            new Member { TenantId = h.Scope.Id, Subject = "lopez-" + Guid.NewGuid(), Name = "Dr. Ensayo López", Role = "doctor" },                                             // the hospital did not offer this one
            new Member { TenantId = h.Scope.Id, Subject = "off-" + Guid.NewGuid(), Name = "Dra. Ficticia Baja", Role = "doctor", WhatsAppPhone = "+503 7000 0003", Disabled = true });
        await h.Db.SaveChangesAsync();
        await h.Say("patient", message);

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.StartsWith("Le pasé tu consulta al equipo del hospital", h.Sent[0]);
        Assert.Equal(offered, h.Interactive.Select(sent => sent.GetRawText()).Where(raw => raw.Contains("https://wa.me/")).Select(raw => System.Text.RegularExpressions.Regex.Match(raw, @"wa\.me/(\d+)").Groups[1].Value).Order());
    }

    [Theory]
    [InlineData("+503 7000 0000", true, true)]
    [InlineData("+503 7000 0000", false, false)] // the hospital did not say the number answers WhatsApp
    [InlineData("132", true, false)]             // a short code opens no chat
    public async Task TappingEmergencyAlsoOffersTheWhatsAppChatWhenTheHospitalNumberHasOne(string phone, bool whatsApp, bool chat)
    {
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.EmergencyPhone = phone; tenant.EmergencyWhatsApp = whatsApp; await h.Db.SaveChangesAsync();
        await h.Say("patient", "EMERGENCIA");

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.StartsWith("Si es una emergencia", h.Sent[0]);
        Assert.Equal(chat ? 2 : 1, h.Sent.Count);
        Assert.Equal(chat, h.Interactive.Any(sent => sent.GetRawText().Contains("https://wa.me/50370000000")));
    }

    [Theory]
    [InlineData("Creo que tomé una sobredosis")]              // an emergency is never a question with buttons
    [InlineData("Quiero hablar con una persona, por favor")]  // the patient already decided
    public async Task EmergencyAndAnExplicitRequestAreStillImmediate(string message)
    {
        await h.Say("patient", message);

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
    }

    [Fact]
    public async Task AWithheldReplyBecomesAnOfferNotATransfer()
    {
        await h.Runtime(h.Model(AgentHarness.Reply("Toma 500 mg de amoxicilina cada 8 horas.")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.DoesNotContain(h.Sent, text => text.Contains("500 mg")); // still withheld
        Assert.Equal(["PERSONA", "MENU"], Buttons());
        Assert.Contains(await Trail(), a => a.Kind == "guard");
    }

    [Fact]
    public async Task AFileTheAgentCannotReadIsOfferedToAPerson()
    {
        h.Db.Add(new Message { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, ExternalId = "file-" + Guid.NewGuid(), Body = "[Archivo recibido]", Sender = "patient", Type = "image" });
        await h.Db.SaveChangesAsync();
        var latest = await h.Db.Messages.OrderByDescending(m => m.CreatedAt).FirstAsync();
        var job = new Job { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, Key = "agent:" + latest.ExternalId }; h.Db.Add(job); await h.Db.SaveChangesAsync();

        await h.Runtime(NoModel, h.Sender()).Run(job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status);
        Assert.Contains("archivo", Assert.Single(h.Sent));
        Assert.Equal(["PERSONA", "MENU"], Buttons());
    }
}
