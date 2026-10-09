using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>«Cómo atiende mi recepción»: the hospital chooses among closed options, ours being the default, and the agent follows them.</summary>
public sealed class AttentionTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    async Task Choose(Attention chosen, string? emergencyPhone = null)
    {
        var tenant = await h.Db.Tenants.SingleAsync(t => t.Id == h.Scope.Id); tenant.Attention = chosen.Write(); tenant.EmergencyPhone = emergencyPhone; await h.Db.SaveChangesAsync();
    }

    [Fact]
    public void NothingStoredOrUnreadableIsTheRecommendedReception()
    {
        Assert.Equal(Attention.Recommended, Attention.Read(null));
        Assert.Equal(Attention.Recommended, Attention.Read("{not json"));
        Assert.Equal(Attention.Recommended, Attention.Read("{\"symptoms\":\"diagnose\"}")); // a choice we do not offer is never honoured
        Assert.Equal("person", Attention.Read("{\"symptoms\":\"person\"}").Symptoms);
        Assert.Equal("doctor", Attention.Read("{\"symptoms\":\"person\"}").Booking);          // what was not chosen stays recommended
    }

    [Theory]
    [InlineData("diagnose", "ask", "doctor", "register", null)]
    [InlineData("ask", "never", "doctor", "register", null)]
    [InlineData("ask", "ask", "random", "register", null)]
    [InlineData("ask", "ask", "doctor", "ignore", null)]
    [InlineData("ask", "ask", "doctor", "register", new string[0])]        // a menu with nothing to tap
    [InlineData("ask", "ask", "doctor", "register", new[] { "BORRAR" })]   // an option the agent does not serve
    public void OnlyTheChoicesOnOfferCanBeSaved(string symptoms, string noSlotSoon, string booking, string unregistered, string[]? menu) =>
        Assert.Throws<ArgumentException>(() => new Attention(symptoms, noSlotSoon, true, booking, unregistered, menu).Checked());

    [Fact]
    public void AMenuWithEveryOptionIsTheRecommendedOne() =>
        Assert.Equal(Attention.Recommended, new Attention(Menu: ["PERSONA", "RECETA", "MISCITAS", "AGENDAR"]).Checked());

    [Fact]
    public async Task TheWelcomeMenuShowsOnlyWhatTheHospitalChose()
    {
        await Choose(new Attention(Menu: ["PERSONA", "AGENDAR"]));
        await h.Say("patient", "Hola");

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal(["AGENDAR", "PERSONA"], AgentHarness.Options(h.Interactive[^1]).Select(option => option.Id));
    }

    [Fact]
    public async Task AHospitalThatPrefersAPersonForSymptomsGetsTheOfferNotTheQuestion()
    {
        await Choose(new Attention(Symptoms: "person"), emergencyPhone: "2200 0000");

        await h.Runtime(h.Model(AgentHarness.ToolCall("handoff", new { reason = "Dolor de pecho", urgent = true }), AgentHarness.Reply("Ok")), h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("agent", (await h.Fresh()).Status); // still the patient's decision
        var sent = Assert.Single(h.Sent);
        Assert.Contains(AgentGuard.PersonQuestion, sent);
        Assert.DoesNotContain(AgentGuard.EmergencyQuestion, sent);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task TheDoctorsChatIsOfferedOnlyIfTheHospitalWantsIt(bool doctorChat, int offered)
    {
        await Choose(new Attention(DoctorChat: doctorChat));
        h.Db.Members.Add(new Member { TenantId = h.Scope.Id, Subject = "rivas-" + Guid.NewGuid(), Name = "Dra. Modelo Rivas", Role = "doctor", WhatsAppPhone = "+503 7000 0001" }); await h.Db.SaveChangesAsync();
        await h.Say("patient", "Quiero hablar con una persona, por favor");

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Equal("human", (await h.Fresh()).Status);
        Assert.Equal(offered, h.Interactive.Count(sent => sent.GetRawText().Contains("https://wa.me/50370000001")));
    }

    [Fact]
    public async Task AHospitalThatRegistersAtReceptionDoesNotOpenTheFormOnWhatsApp()
    {
        await Choose(new Attention(Unregistered: "person"));
        await h.Say("patient", $"CITA {DateTimeOffset.UtcNow.AddDays(2):yyyy-MM-ddTHH:mm:sszzz} {Guid.NewGuid()} 30 Dra. Modelo Rivas");

        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Contains("recepción te registra", Assert.Single(h.Sent));
        Assert.DoesNotContain(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "intake");
    }
}
