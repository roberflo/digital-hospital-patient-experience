using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Recepcion;
using Recepcion.Integrations;
using Xunit;

/// <summary>RXE-C (Hospital docs/features/19-odontologia/radiografias-en-el-expediente.spec.md): a clinician files a file the patient sent
/// by WhatsApp into Hospital's record, with their own token, through Recepción's server; Recepción keeps only the returned id.</summary>
public sealed class MessageToRecordTests : IAsyncLifetime
{
    readonly AgentHarness h = new();
    public Task InitializeAsync() => h.Start();
    public Task DisposeAsync() => h.DisposeAsync().AsTask();

    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]; // synthetic bytes, never a real study
    readonly Guid attachment = Guid.NewGuid();
    readonly List<(string Path, string? Bearer, string? ContentType, Dictionary<string, string> Fields, byte[]? File, string? FileName)> uploads = [];
    string? user;
    string User => user ??= "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { tenant_id = h.Scope.Id, sub = "doc-1", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic";
    readonly CurrentUser doctor = new() { Subject = "doc-1", Role = "doctor", Name = "Dra. Sintética" };

    AgentHarness.Fake Kapso() => new(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/files/synthetic"
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) }
        : AgentHarness.Json(new { url = "https://api.kapso.ai/files/synthetic", mime_type = "image/png" })));

    /// <summary>Hospital's upload route: records what arrived (read while the request is alive) and answers <paramref name="answer"/> or 201.</summary>
    AgentHarness.Fake Hospital(Func<HttpResponseMessage>? answer = null) => new(async request =>
    {
        var fields = new Dictionary<string, string>(); byte[]? file = null; string? name = null;
        if (request.Content is MultipartFormDataContent form)
            foreach (var part in form)
            {
                var field = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (field == "file") { file = await part.ReadAsByteArrayAsync(); name = part.Headers.ContentDisposition.FileNameStar ?? part.Headers.ContentDisposition.FileName?.Trim('"'); }
                else fields[field] = await part.ReadAsStringAsync();
            }
        uploads.Add((request.Method + " " + request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter, request.Content?.Headers.ContentType?.MediaType, fields, file, name));
        return answer?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(JsonSerializer.Serialize(new { attachmentId = attachment }), Encoding.UTF8, "application/json") };
    });

    async Task<Message> Inbound(string type = "image", string sender = "patient", string? mediaId = "media-synthetic", string? name = null)
    {
        var m = new Message { TenantId = h.Scope.Id, ConversationId = h.Conversation.Id, ExternalId = "m-" + Guid.NewGuid(), Sender = sender, Type = type, MediaId = mediaId, MediaName = name, Body = "[imagen]" };
        h.Db.Add(m); await h.Db.SaveChangesAsync(); return m;
    }
    HttpContext Request() { var ctx = new DefaultHttpContext(); ctx.Request.Headers.Authorization = "Bearer " + User; return ctx; }
    Task<IResult> Save(Message m, CurrentUser who, AgentHarness.Fake kapso, AgentHarness.Fake hospital, RecordAttachmentInput? input = null)
    {
        var k = new KapsoClient(new HttpClient(kapso), h.Config());
        return WhatsAppEndpoints.SaveToRecord(m.Id, input ?? new RecordAttachmentInput(null, null, null), Request(), h.Db, h.Scope, who, k, h.Client(hospital), new ConversationService(h.Db, h.Scope, k));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("admin")]
    public async Task RXE_C2_ANonClinicalProfileIsRefusedWithoutCallingHospitalOrKapso(string role)
    {
        await h.Link(); var m = await Inbound(); var kapso = Kapso(); var hospital = Hospital();
        await Assert.ThrowsAsync<AccessDeniedException>(() => Save(m, new CurrentUser { Subject = "staff-1", Role = role }, kapso, hospital));
        Assert.Equal(0, hospital.Calls); Assert.Equal(0, kapso.Calls);
    }

    [Fact]
    public async Task RXE_C2_C3_AClinicianFilesTheFileMultipartWithTheirOwnTokenAndRecepcionKeepsNoBytes()
    {
        await h.Link(); var m = await Inbound(name: "rx-sintetica.png"); var messages = await h.Db.Messages.CountAsync(); var activities = await h.Db.Activities.CountAsync();
        var clinicalDate = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        var result = await Save(m, doctor, Kapso(), Hospital(), new RecordAttachmentInput("prior-study", clinicalDate, "  Periapical sintética  "));

        Assert.Equal(attachment, Assert.IsType<Ok<RecordAttachment>>(result).Value!.AttachmentId);
        var upload = Assert.Single(uploads);
        Assert.Equal($"POST /v1/patients/{h.Contact.PatientId:D}/attachments", upload.Path);
        Assert.Equal(User, upload.Bearer); // the person's token: never the service account
        Assert.Equal("multipart/form-data", upload.ContentType);
        Assert.Equal(Png, upload.File); Assert.Equal("rx-sintetica.png", upload.FileName);
        Assert.Equal("prior-study", upload.Fields["categoryCode"]);
        Assert.Equal(clinicalDate, DateTimeOffset.Parse(upload.Fields["clinicalDate"]));
        Assert.Equal("Periapical sintética", upload.Fields["description"]);
        Assert.False(upload.Fields.ContainsKey("encounterId"));
        // Nothing of the file is kept here: no new message, activity or bytes; only the id Hospital returned.
        Assert.Equal(messages, await h.Db.Messages.CountAsync()); Assert.Equal(activities, await h.Db.Activities.CountAsync());
        var stored = await h.NewContext().Messages.AsNoTracking().SingleAsync(x => x.Id == m.Id);
        Assert.Equal(attachment, stored.HospitalAttachmentId); Assert.Equal("[imagen]", stored.Body);
    }

    [Fact]
    public async Task RXE_C3_WithoutChoicesTheCategoryIsPriorStudyAndAFileWithoutNameStillHasOne()
    {
        await h.Link(); var m = await Inbound();
        await Save(m, doctor, Kapso(), Hospital());
        var upload = Assert.Single(uploads);
        Assert.Equal("prior-study", upload.Fields["categoryCode"]);
        Assert.False(upload.Fields.ContainsKey("clinicalDate")); Assert.False(upload.Fields.ContainsKey("description"));
        Assert.False(string.IsNullOrWhiteSpace(upload.FileName)); // Hospital requires one (documents.attachment_filename_required)
    }

    [Fact]
    public async Task RXE_C4_SavingTheSameMessageTwiceReturnsTheSameIdWithoutCallingHospitalAgain()
    {
        await h.Link(); var m = await Inbound(); var hospital = Hospital(); var kapso = Kapso();
        var first = Assert.IsType<Ok<RecordAttachment>>(await Save(m, doctor, kapso, hospital)).Value!.AttachmentId;
        var second = Assert.IsType<Ok<RecordAttachment>>(await Save(m, doctor, kapso, hospital)).Value!.AttachmentId;
        Assert.Equal(first, second); Assert.Equal(1, hospital.Calls); Assert.Equal(2, kapso.Calls); // one download: metadata + bytes
    }

    [Theory]
    [InlineData("image", "human", true)]   // outbound
    [InlineData("image", "agent", true)]
    [InlineData("text", "patient", true)]
    [InlineData("audio", "patient", true)]
    [InlineData("image", "patient", false)] // contact not linked
    public async Task RXE_C1_OnlyAnInboundImageOrDocumentOfALinkedContactIsFiled(string type, string sender, bool linked)
    {
        if (linked) await h.Link();
        var m = await Inbound(type, sender, type == "text" ? null : "media-synthetic"); var hospital = Hospital(); var kapso = Kapso();
        await Assert.ThrowsAsync<ArgumentException>(() => Save(m, doctor, kapso, hospital));
        Assert.Equal(0, hospital.Calls); Assert.Equal(0, kapso.Calls);
    }

    public static TheoryData<int, string?, bool, string, int> Failures => new()
    {
        { 400, "documents.attachment_media_type_unsupported", false, "documents.attachment_media_type_unsupported", 400 },
        { 403, null, false, "hospital.user_forbidden", 403 },
        { 404, null, false, "hospital.patient_not_found", 404 },
        { 503, null, false, "hospital.unavailable", 503 },
        { 0, null, false, "hospital.unavailable", 503 },               // connection refused
        { 0, null, true, "hospital.attachment_unconfirmed", 504 },     // timeout: Hospital may have saved it
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task RXE_C5_EachFailureIsNamedWithoutPhiAndAuditedWithItsResult(int status, string? code, bool timeout, string expected, int expectedStatus)
    {
        await h.Link(); var m = await Inbound(name: "rx-sintetica.png");
        var hospital = Hospital(() => status == 0
            ? throw (timeout ? new TaskCanceledException("t", new TimeoutException()) : new HttpRequestException("synthetic refused"))
            : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { code = code ?? "patient.not_found", title = "Paciente Sintético" }), Encoding.UTF8, "application/problem+json") });

        var ex = await Assert.ThrowsAsync<HospitalIntegrationException>(() => Save(m, doctor, Kapso(), hospital, new RecordAttachmentInput(null, null, "Periapical sintética")));

        Assert.Equal(expected, ex.Code); Assert.Equal(expectedStatus, (int)ex.StatusCode);
        Assert.DoesNotContain("Sintétic", ex.Message + ex.DisplayMessage);
        Assert.NotEqual(new HospitalIntegrationException("hospital.unknown", HttpStatusCode.BadGateway).DisplayMessage, ex.DisplayMessage); // a named message, not the generic one
        await using var fresh = h.NewContext();
        Assert.Null((await fresh.Messages.AsNoTracking().SingleAsync(x => x.Id == m.Id)).HospitalAttachmentId);
        var audit = await fresh.Audits.AsNoTracking().SingleAsync(x => x.Action.StartsWith("hospital_attachment."));
        Assert.Equal("doc-1", audit.Actor); Assert.Equal("hospital_attachment.failed:" + expected, audit.Action);
        Assert.Equal($"contact:{h.Contact.Id:D} message:{m.Id:D}", audit.Resource);
    }

    [Fact]
    public async Task RXE_C5_ASavedFileIsAuditedWithActorContactMessageAndResultNeverItsContent()
    {
        await h.Link(); var m = await Inbound(name: "rx-sintetica.png");
        await Save(m, doctor, Kapso(), Hospital(), new RecordAttachmentInput(null, null, "Periapical sintética"));
        await using var fresh = h.NewContext();
        var audit = await fresh.Audits.AsNoTracking().SingleAsync(x => x.Action.StartsWith("hospital_attachment."));
        Assert.Equal("doc-1", audit.Actor); Assert.Equal("hospital_attachment.saved", audit.Action);
        Assert.Equal($"contact:{h.Contact.Id:D} message:{m.Id:D}", audit.Resource);
        Assert.DoesNotContain("sintética", audit.Action + audit.Resource, StringComparison.OrdinalIgnoreCase);
    }
}
