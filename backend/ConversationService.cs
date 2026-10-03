using System.Data;
using Microsoft.EntityFrameworkCore;
using Recepcion.Integrations;
namespace Recepcion;

public sealed class ConversationService(CrmDb db, TenantScope scope, KapsoClient kapso)
{
    public async Task<Message> Send(Guid id, string body, string sender, string requestKey, string? mediaId = null, string type = "text", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 100) throw new ArgumentException("Idempotency-Key requerido");
        using var lease = await Lock(id, ct);
        var existing = await db.Messages.SingleOrDefaultAsync(x => x.RequestKey == requestKey, ct); if (existing is not null) { if (existing.ConversationId != id || existing.Body != body || existing.MediaId != mediaId || existing.Type != type) throw new ArgumentException("Clave de envío reutilizada"); return existing; }
        var conv = await db.Conversations.SingleAsync(x => x.Id == id, ct); var channel = await db.Channels.SingleAsync(x => x.Id == conv.ChannelId, ct); var contact = await db.Contacts.SingleAsync(x => x.Id == conv.ContactId, ct);
        if (!channel.Enabled) throw new ArgumentException("Canal desactivado");
        if (!Rules.WithinWindow(conv.LastInboundAt, DateTimeOffset.UtcNow)) throw new ArgumentException("Ventana de WhatsApp cerrada. Espera un mensaje del paciente o utiliza una plantilla aprobada desde Kapso.");
        var message = new Message { TenantId = scope.Id, ConversationId = id, Body = body, Sender = sender, RequestKey = requestKey, MediaId = mediaId, Type = type, Status = "sending" }; db.Add(message); await db.SaveChangesAsync(ct);
        try
        {
            message.ExternalId = await kapso.Send(channel.PhoneNumberId, contact.Phone, body, mediaId, type, ct); message.Status = "sent";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException)
        {
            // The remote operation may have succeeded: retain the attempted message and never retry automatically.
            message.Status = ex is ArgumentException ? "failed" : "uncertain"; conv.Status = "human";
            db.Activities.Add(new Activity { TenantId = scope.Id, ConversationId = id, ContactId = conv.ContactId, Kind = "delivery", Actor = "Sistema", Body = message.Status == "uncertain" ? "Entrega sin confirmar. Comprueba WhatsApp antes de volver a enviar." : "El envío no está habilitado o fue rechazado localmente." });
        }
        conv.LastMessage=body.Length>160?body[..160]:body;conv.Revision++; conv.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(CancellationToken.None); return message;
    }
    public async Task<IDisposable> Lock(Guid id, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        var conn = db.Database.GetDbConnection(); var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT pg_advisory_lock(@id)";
        var p = cmd.CreateParameter(); p.ParameterName = "id"; p.Value = BitConverter.ToInt64(id.ToByteArray(), 0); cmd.Parameters.Add(p); await cmd.ExecuteNonQueryAsync(ct); cmd.Dispose();
        return new Lease(conn, (long)p.Value);
    }
    sealed class Lease(System.Data.Common.DbConnection connection, long key) : IDisposable
    {
        public void Dispose() { if (connection.State == ConnectionState.Open) { using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT pg_advisory_unlock(@id)"; var p = cmd.CreateParameter(); p.ParameterName = "id"; p.Value = key; cmd.Parameters.Add(p); cmd.ExecuteNonQuery(); } }
    }
}
