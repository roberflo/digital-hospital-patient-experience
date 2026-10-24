using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Xunit;

/// <summary>docs/agent-evidence.md: a registration datum reaches the form only if the patient wrote it in the conversation in progress.
/// What only the model says is asked. Synthetic people only.</summary>
public sealed class AgentEvidenceTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();
    static readonly AgentHarness.Fake NoModel = new(_ => throw new InvalidOperationException("The model must not be consulted"));
    /// <summary>Irene's seven data, as the model would pass them.</summary>
    static readonly object E = new { givenNames = "Irene Sintética", familyNames = "Mora Prueba", birthDate = "1979-09-09", sex = "female", emergencyContactName = "Sofía Sintética", emergencyContactRelationship = "hija", emergencyContactPhone = "70000007" };
    /// <summary>What Irene wrote of them.</summary>
    const string S = "Soy Irene Sintética Mora Prueba, mujer, nací el 9 de septiembre de 1979. Mi hija Sofía Sintética, 7000-0007.";

    Task Run(string tool, object arguments) => h.Runtime(h.Model(AgentHarness.ToolCall(tool, arguments), AgentHarness.Reply("Revisa tus datos y confirma.")), h.Sender()).Run(h.Job, CancellationToken.None);
    /// <summary>INV-EV-5: a withheld value is kept nowhere: no activity, no working state, no message sent.</summary>
    async Task NowhereHolds(params string[] values)
    {
        var bodies = (await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).Select(a => a.Body).ToListAsync())
            .Concat(await h.Db.PendingActions.Where(a => a.ConversationId == h.Conversation.Id).Select(a => a.Body).ToListAsync()).Concat(h.Sent).ToList();
        Assert.All(values, value => Assert.DoesNotContain(bodies, body => body.Contains(value)));
    }

    [Fact]
    public async Task NoToolOffersTheModelAConfidenceAScoreOrASource()
    {
        // EV-1 / INV-EV-4.
        JsonElement? request = null;
        var model = new AgentHarness.Fake(async message => { request ??= JsonDocument.Parse(await message.Content!.ReadAsStringAsync()).RootElement.Clone(); return AgentHarness.Reply("Hola."); });

        await h.Runtime(model, h.Sender()).Run(h.Job, CancellationToken.None);

        var tools = Assert.NotNull(request).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function")).ToList();
        Assert.Equal(16, tools.Count); // hospital_agreement, the 17th, is offered only to a hospital with agreements on
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "start_registration");
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "propose_registration");
        var parameters = tools.SelectMany(t => t.GetProperty("parameters").GetProperty("properties").EnumerateObject().Select(p => $"{t.GetProperty("name").GetString()}.{p.Name}")).ToList();
        Assert.DoesNotContain(parameters, p => Regex.IsMatch(p.Split('.')[1], "(?i)confiden|confianza|score|puntaje|certeza|url|source|fuente|evidenc"));
    }

    [Fact]
    public async Task WhatThePatientNeverWroteIsAskedAndKeptNowhere()
    {
        // EV-2 / INV-EV-1 / EV-9: the patient only wrote «Consulta sintética».
        await Run("start_registration", E);

        Assert.Contains("¿Cuál es tu nombre?", h.Sent[^1]);
        Assert.Empty(await h.Pending("proposal"));
        await NowhereHolds("Irene", "1979", "70000007");
    }

    [Fact]
    public async Task ASexThePatientNeverWroteIsAsked()
    {
        // EV-2: everything else was written.
        await h.Say("patient", S.Replace("mujer, ", ""));

        await Run("start_registration", E);

        Assert.Contains("documento de identidad", h.Sent[^1]);
        Assert.Empty(await h.Pending("proposal"));
    }

    [Theory]
    [InlineData("Soy Pedro Sintético Prueba, nací el 2 de febrero de 1980. Soy hombre, mi contacto es mi mujer Ana Sintética, 7000 0003.")]
    [InlineData("Soy Pedro Sintético Prueba, hombre, nací el 2 de febrero de 1980. Mi hermana Ana Sintética, 7000 0003.")]
    public async Task TheSexIsAlwaysTappedNeverTakenFromTheModel(string said)
    {
        // EV-8 (owner, 2026-10-23): «mi mujer Ana» is someone else's sex, so the sex of the document is always asked with the two buttons,
        // even when the patient wrote it. Only the sex is asked: the rest written is kept.
        await h.Say("patient", said);

        await Run("start_registration", new { givenNames = "Pedro Sintético", familyNames = "Prueba", birthDate = "2 de febrero de 1980", sex = "male", emergencyContactName = "Ana Sintética", emergencyContactPhone = "7000 0003" });

        Assert.Contains("documento de identidad", h.Sent[^1]); Assert.Contains("me falta uno", h.Sent[^1]);
        Assert.Empty(await h.Pending("proposal"));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(Assert.Single(await h.Pending("intake")).Body).RootElement.GetProperty("sex").ValueKind);
    }

    [Fact]
    public async Task ADateAndAPhoneAreComparedAsADateAndAPhone()
    {
        // EV-4 (a) «9 de septiembre de 1979» is 09/09/1979, (d) «7000-0007» is 70000007: both are taken.
        await h.Say("patient", S);

        await Run("start_registration", new { givenNames = "Irene Sintética", familyNames = "Mora Prueba", birthDate = "09/09/1979", sex = "female", emergencyContactName = "Sofía Sintética", emergencyContactRelationship = "hija", emergencyContactPhone = "70000007" });
        Assert.Contains("me falta uno", h.Sent[^1]); // only the sex, always tapped (EV-8)
        await h.Say("patient", "f"); await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Contains("Nacimiento: 9 de septiembre de 1979", h.Sent[^1]); Assert.Contains("Sofía Sintética (hija), 70000007", h.Sent[^1]);
        Assert.Single(await h.Pending("proposal"));
    }

    [Fact]
    public async Task ADateIsReadDayFirstAsTheFormReadsIt()
    {
        // EV-4 (b): «03/04/1990» is the 3rd of April; the model's «1990-03-04» is another day.
        await h.Say("patient", "Soy Irene Sintética Mora Prueba, mujer, nací el 03/04/1990. Mi hija Sofía Sintética, 7000-0007.");

        await Run("start_registration", new { givenNames = "Irene Sintética", familyNames = "Mora Prueba", birthDate = "1990-03-04", sex = "female", emergencyContactName = "Sofía Sintética", emergencyContactRelationship = "hija", emergencyContactPhone = "70000007" });

        Assert.Contains("¿Cuál es tu fecha de nacimiento?", h.Sent[^1]);
        Assert.Empty(await h.Pending("proposal"));
    }

    [Fact]
    public async Task ANameIsComparedWordByWordNeverApproximately()
    {
        // EV-4 (c): «Marchetta» written, «Marchetti» passed. Only the surnames are asked; the rest is kept.
        await h.Say("patient", "Soy Irene Sintética Marchetta Prueba, mujer, nací el 9 de septiembre de 1979. Mi hija Sofía Sintética, 7000-0007.");

        await Run("start_registration", new { givenNames = "Irene Sintética", familyNames = "Marchetti Prueba", birthDate = "1979-09-09", sex = "female", emergencyContactName = "Sofía Sintética", emergencyContactRelationship = "hija", emergencyContactPhone = "70000007" });

        Assert.Contains("¿Y tus apellidos?", h.Sent[^1]);
        Assert.Empty(await h.Pending("proposal"));
        var form = JsonDocument.Parse(Assert.Single(await h.Pending("intake")).Body).RootElement;
        Assert.Equal(JsonValueKind.Null, form.GetProperty("familyNames").ValueKind);
        Assert.Equal(("1979-09-09", "70000007"), (form.GetProperty("birthDate").GetString(), form.GetProperty("emergencyPhone").GetString()));
    }

    [Fact]
    public async Task AnAdultDateOnlyTheModelGaveIsAskedAndKeptNowhere()
    {
        // EV-5 (a) / INV-EV-2 / EV-9.
        await h.Say("patient", "Soy Irene Sintética Mora Prueba.");

        await Run("start_registration", E);

        Assert.Contains("¿Cuál es tu fecha de nacimiento?", h.Sent[^1]);
        Assert.Empty(await h.Pending("proposal"));
        await NowhereHolds("1979", "70000007", "Sofía");
    }

    [Fact]
    public async Task AMinorIsNotMadeAnAdultByTheModelsDate()
    {
        // EV-5 (b) / INV-EV-2: the patient wrote 2010, the model says 1990.
        await h.Say("patient", "Soy Diego Sintético Prueba, nací el 01/01/2010.");

        await Run("start_registration", new { givenNames = "Diego Sintético", familyNames = "Prueba", birthDate = "1990-01-01", sex = "male", emergencyContactName = "Marta Sintética", emergencyContactRelationship = "madre", emergencyContactPhone = "70000002" });

        Assert.Contains("¿Cuál es tu fecha de nacimiento?", h.Sent[^1]);
        Assert.Empty(await h.Pending("proposal"));
        Assert.DoesNotContain(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "handoff_offer");

        await h.Say("patient", "01/01/2010");
        await h.Runtime(NoModel, h.Sender()).Run(h.Job, CancellationToken.None);

        Assert.Contains(await h.Db.Activities.Where(a => a.ConversationId == h.Conversation.Id).ToListAsync(), a => a.Kind == "handoff_offer");
    }

    [Fact]
    public async Task TwoBirthDatesThePatientWroteAreBothWithheld()
    {
        // EV-6 / INV-EV-3: the model passing one of them does not choose it.
        await h.Say("patient", "Soy Irene Sintética Mora Prueba");
        await h.Say("patient", "nací el 12/03/1990");
        await h.Say("patient", "perdón, 12/03/1991");

        await Run("start_registration", new { givenNames = "Irene Sintética", familyNames = "Mora Prueba", birthDate = "1991-03-12", sex = "female" });

        Assert.Contains("¿Cuál es tu fecha de nacimiento?", h.Sent[^1]);
        Assert.Empty(await h.Pending("proposal"));
    }

    [Fact]
    public async Task AProposedRegistrationThePatientNeverWroteLeavesNoCardAndReachesNoHospital()
    {
        // EV-7: the model calls propose_registration with values the patient never wrote, and the patient says «sí» after.
        var posts = new List<string>();
        var hospital = new AgentHarness.Fake(request => { if (request.RequestUri!.AbsolutePath == "/v1/patients") posts.Add(request.Method.Method); return Task.FromResult(AgentHarness.Json(new { })); });
        await h.Runtime(h.Model(AgentHarness.ToolCall("propose_registration", E), AgentHarness.Reply("Revisa tus datos y confirma.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Empty(await h.Pending("proposal"));
        Assert.DoesNotContain(h.Sent, text => text.Contains("Revisa tus datos"));

        await h.Say("patient", "sí");
        await h.Runtime(h.Model(AgentHarness.Reply("Listo.")), h.Sender(), hospital).Run(h.Job, CancellationToken.None);

        Assert.Empty(posts);
        Assert.Empty(await h.Pending("proposal"));
    }
}
