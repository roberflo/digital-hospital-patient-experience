using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Security.Cryptography;
using System.Text;

namespace Recepcion;

public sealed class TenantScope { public Guid Id { get; set; } }
public abstract class TenantRow { public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } }
public sealed class Tenant
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? HospitalConnection { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "";
    /// <summary>«reception» (the hospital uses Recepción) or «reminders»: a hospital without Recepción whose appointment reminders go out from the
    /// platform's general number (recordatorios-whatsapp.spec.md S3). Set only by the platform operator; nobody of a «reminders» hospital signs in.</summary>
    public string Mode { get; set; } = "reception";
    public string Guide { get; set; } = ""; public bool AgentEnabled { get; set; }
    public string TimeZone { get; set; } = "America/El_Salvador";
    /// <summary>The number the agent gives a patient it hands off; shown as written by the hospital.</summary>
    public string? EmergencyPhone { get; set; }
    /// <summary>The hospital says that number answers WhatsApp: an emergency also gets a button to write to it.</summary>
    public bool EmergencyWhatsApp { get; set; }
    /// <summary>How this hospital chose to attend, as <see cref="Recepcion.Attention"/> JSON. Null is the recommended reception.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Attention { get; set; }
    public bool RemindersEnabled { get; set; }
    public Guid? ReminderChannelId { get; set; }
    public string ReminderDayTemplate { get; set; } = ReminderRules.DayTemplateV4;
    public string ReminderHourTemplate { get; set; } = "recepcion_cita_una_hora_v1";
    public string ReminderLanguage { get; set; } = "es";
    public DateTimeOffset? ReminderLastSyncAt { get; set; }
    public string? ReminderSyncError { get; set; }
    public string? KapsoCustomerId { get; set; }
    public string? GoogleRefreshToken { get; set; }
    public string? GoogleCalendarId { get; set; }
    public string? GoogleStateHash { get; set; }
    public DateTimeOffset? GoogleStateExpires { get; set; }
}
public sealed class Member : TenantRow
{
    public string Subject { get; set; } = ""; public string Name { get; set; } = "";
    public string Role { get; set; } = "agent"; public bool Disabled { get; set; }
    /// <summary>Set by the hospital for a doctor patients may write to: the agent offers it when a patient asks for a person.</summary>
    public string? WhatsAppPhone { get; set; }
}
public sealed class Contact : TenantRow
{
    public Guid? HospitalCustomerId { get; set; }
    public Guid? HospitalCompanyId { get; set; }
    public string? HospitalCompanyName { get; set; }
    public DateTimeOffset? CustomerSince { get; set; }
    public string? CustomerSource { get; set; }
    public DateTimeOffset? CommercialSyncedAt { get; set; }
    public bool IsCustomer => HospitalCustomerId is not null;
    public string Name { get; set; } = ""; public string Phone { get; set; } = "";
    public string PhoneHash { get; set; } = ""; public string Email { get; set; } = "";
    public Guid? CompanyId { get; set; }
    public string LifecycleStage { get; set; } = "lead";
    public DateTimeOffset? ReminderConsentAt { get; set; }
    public Guid? PatientId { get; set; }
    public string Tags { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Company : TenantRow { public string Name { get; set; } = ""; public string Industry { get; set; } = ""; public string Email { get; set; } = ""; public string Phone { get; set; } = ""; }
public sealed class Opportunity : TenantRow
{
    public string? HospitalQuote { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? HospitalPurchaseRequest { get; set; }
    public bool PurchasePending => HospitalPurchaseRequest is not null && HospitalPurchaseId is null;
    public string? PaymentReference => HospitalPurchaseRequest is null ? null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(HospitalPurchaseRequest).GetProperty("paymentReference").GetString();
    public Guid? HospitalPurchaseId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Title { get; set; } = ""; public Guid ContactId { get; set; }
    public decimal Value { get; set; }
    public string Stage { get; set; } = "new";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Activity : TenantRow
{
    public string ActorRole { get; set; } = "unknown";
    public string? ActorSubject { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid? ConversationId { get; set; }
    public string Kind { get; set; } = "note"; public string Body { get; set; } = "";
    public string Actor { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
/// <summary>What the agent is waiting on in one conversation: a proposal to confirm, the registration form, the question just asked, what the
/// patient said an appointment is for (MR-40). Working state, not history: it expires, is consumed, and the daily sweep removes it. The answer is
/// matched to the message that asked (<see cref="PromptMessageId"/>), never to that message's text.</summary>
public sealed class PendingAction : TenantRow
{
    public Guid ConversationId { get; set; }
    /// <summary>proposal, intake, question or visit_kind.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The proposal's confirmation code, the question asked (emergency, person, emergency-contact: «Emergencia: …» in the answer labels that
    /// field, MR-35) or the visit kind.</summary>
    public string Code { get; set; } = "";
    public string Body { get; set; } = "";
    public Guid? PromptMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
/// <summary>One durable preference a patient stated about themselves, kept for later conversations: one row per contact and key
/// (docs/reception-agent.md, criterio 57). Never clinical content.</summary>
public sealed class ContactMemory : TenantRow
{
    public Guid ContactId { get; set; }
    public string Key { get; set; } = ""; public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Channel : TenantRow
{
    public string Name { get; set; } = ""; public string PhoneNumberId { get; set; } = "";
    public string? DoctorId { get; set; }
    public bool Coexistence { get; set; }
    public bool Enabled { get; set; }
    public string? KapsoCustomerId { get; set; }
    public DateTimeOffset? LastWebhookAt { get; set; }
}
public sealed class Conversation : TenantRow
{
    public string State { get; set; } = "open";
    public string Priority { get; set; } = "normal";
    public string Labels { get; set; } = "";
    public DateTimeOffset? SnoozedUntil { get; set; }
    public string? LastMessage { get; set; }
    public Guid ContactId { get; set; }
    public Guid ChannelId { get; set; }
    public string ExternalId { get; set; } = ""; public string Status { get; set; } = "human";
    public string? AssignedTo { get; set; }
    public string Summary { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastInboundAt { get; set; }
    public long Revision { get; set; }
}
public sealed class ConversationRead : TenantRow {
    public Guid ConversationId { get; set; }
    public string Subject { get; set; } = "";
    public DateTimeOffset LastReadAt { get; set; }
}
public sealed class SavedReply : TenantRow {
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}
public sealed class InboxView : TenantRow {
    public string Subject { get; set; } = "";
    public string Name { get; set; } = "";
    public string Filters { get; set; } = "";
}
public sealed class ConversationMacro : TenantRow {
    public string Name { get; set; } = "";
    public string? State { get; set; }
    public string? Priority { get; set; }
    public string Labels { get; set; } = "";
    public string Note { get; set; } = "";
    public bool TakeOwnership { get; set; }
}
public sealed class Message : TenantRow
{
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsHistory { get; set; }
    public Guid ConversationId { get; set; }
    public string? ExternalId { get; set; }
    public string Sender { get; set; } = "patient"; public string Body { get; set; } = "";
    public string Type { get; set; } = "text"; public string? MediaId { get; set; }
    public string? MediaName { get; set; }
    public string Status { get; set; } = "received";
    public string? RequestKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class AppointmentReminder : TenantRow
{
    /// <summary>Null on a reminder of the general number (S3): it has no contact and no channel, only <see cref="RecipientHash"/>.</summary>
    public Guid? ContactId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? ChannelId { get; set; }
    /// <summary>General number only: HMAC of the phone Hospital named as recipient (criterio 41). The phone itself is never stored.</summary>
    public string? RecipientHash { get; set; }
    /// <summary>General number only: a BAJA arrived after this was sent and Hospital has not yet recorded the withdrawal (criterio 25b).</summary>
    public DateTimeOffset? OptOutPendingAt { get; set; }
    /// <summary>General number only: when Hospital recorded that withdrawal. A later appearance of the patient means a new authorisation (A20).</summary>
    public DateTimeOffset? OptOutWithdrawnAt { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid? MessageId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public string Window { get; set; } = "day_before";
    public string Status { get; set; } = "pending";
    public string? Reason { get; set; }
    public DateTimeOffset? AttemptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
/// <summary>The general number's opt-out list (criterio 25, A17): only the phone's HMAC, no hospital, patient or body. Not a TenantRow.</summary>
public sealed class GeneralOptOut { public string PhoneHash { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } }
/// <summary>The general number's one-fixed-reply-per-24-h cap (criterios 24, 26): the phone's HMAC and the last reply, nothing else. Not a TenantRow.</summary>
public sealed class GeneralReplyCap { public string PhoneHash { get; set; } = ""; public DateTimeOffset LastReplyAt { get; set; } }
public sealed class Job : TenantRow
{
    public Guid ConversationId { get; set; }
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "agent"; public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public string? Error { get; set; }
}
public sealed class Receipt : TenantRow { public string Key { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class CalendarLink : TenantRow { public Guid AppointmentId { get; set; } public string GoogleEventId { get; set; } = ""; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class Audit : TenantRow { public string Actor { get; set; } = ""; public string Action { get; set; } = ""; public string Resource { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }

public sealed class CrmDb(DbContextOptions<CrmDb> options, TenantScope scope, IDataProtectionProvider protection) : DbContext(options)
{
    public DbSet<AppointmentReminder> AppointmentReminders => Set<AppointmentReminder>();
    public DbSet<GeneralOptOut> GeneralOptOuts => Set<GeneralOptOut>(); public DbSet<GeneralReplyCap> GeneralReplyCaps => Set<GeneralReplyCap>();
    public DbSet<Tenant> Tenants => Set<Tenant>(); public DbSet<Member> Members => Set<Member>();
    public DbSet<Contact> Contacts => Set<Contact>(); public DbSet<Company> Companies => Set<Company>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>(); public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<ContactMemory> ContactMemories => Set<ContactMemory>();
    public DbSet<PendingAction> PendingActions => Set<PendingAction>();
    public DbSet<Channel> Channels => Set<Channel>(); public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>(); public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Receipt> Receipts => Set<Receipt>(); public DbSet<Audit> Audits => Set<Audit>();
    public DbSet<ConversationRead> ConversationReads => Set<ConversationRead>();
    public DbSet<InboxView> InboxViews => Set<InboxView>();
    public DbSet<ConversationMacro> Macros => Set<ConversationMacro>();
    public DbSet<SavedReply> SavedReplies => Set<SavedReply>();
    public DbSet<CalendarLink> CalendarLinks => Set<CalendarLink>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        Map<AppointmentReminder>(b);
        b.Entity<AppointmentReminder>().HasIndex(x => new { x.TenantId, x.AppointmentId, x.StartsAt, x.Window }).IsUnique();
        b.Entity<AppointmentReminder>().HasIndex(x => new { x.TenantId, x.Status, x.DueAt });
        b.Entity<AppointmentReminder>().HasIndex(x => x.RecipientHash); // BAJA finds the general number's sends by hash, across hospitals
        b.Entity<GeneralOptOut>().HasKey(x => x.PhoneHash); b.Entity<GeneralReplyCap>().HasKey(x => x.PhoneHash); b.Entity<GeneralReplyCap>().HasIndex(x => x.LastReplyAt); // the 24 h purge
        b.Entity<AppointmentReminder>().HasOne<Contact>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContactId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AppointmentReminder>().HasOne<Channel>().WithMany().HasForeignKey(x => new { x.TenantId, x.ChannelId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Map<ConversationRead>(b); Map<SavedReply>(b); Map<InboxView>(b); Map<ConversationMacro>(b);
        b.Entity<InboxView>().HasIndex(x=>new{x.TenantId,x.Subject});
        Map<Member>(b); Map<Contact>(b); Map<Company>(b); Map<Opportunity>(b); Map<Activity>(b); Map<Channel>(b);
        Map<Conversation>(b); Map<Message>(b); Map<Job>(b); Map<Receipt>(b); Map<Audit>(b); Map<CalendarLink>(b);
        b.Entity<ConversationRead>().HasIndex(x => new {x.TenantId, x.ConversationId, x.Subject}).IsUnique();
        b.Entity<ConversationRead>().HasOne<Conversation>().WithMany().HasForeignKey(x=>new{x.TenantId,x.ConversationId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Contact>().HasOne<Company>().WithMany().HasForeignKey(x=>new{x.TenantId,x.CompanyId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Opportunity>().HasOne<Conversation>().WithMany().HasForeignKey(x=>new{x.TenantId,x.ConversationId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Activity>().HasIndex(x => new { x.TenantId, x.CreatedAt, x.Id });
        // What the agent reads every turn: a conversation's latest messages and trail, and a contact's past actions and preferences.
        b.Entity<Activity>().HasIndex(x => new { x.TenantId, x.ConversationId, x.CreatedAt }); b.Entity<Activity>().HasIndex(x => new { x.TenantId, x.ContactId, x.CreatedAt });
        b.Entity<Message>().HasIndex(x => new { x.TenantId, x.ConversationId, x.CreatedAt });
        Map<PendingAction>(b); b.Entity<PendingAction>().HasIndex(x => new { x.TenantId, x.ConversationId, x.Kind, x.ExpiresAt });
        b.Entity<PendingAction>().HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConversationId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Map<ContactMemory>(b); b.Entity<ContactMemory>().HasIndex(x => new { x.TenantId, x.ContactId, x.Key }).IsUnique();
        b.Entity<ContactMemory>().HasOne<Contact>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContactId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Member>().HasIndex(x => x.Subject).IsUnique();
        b.Entity<Tenant>().HasIndex(x => x.KapsoCustomerId).IsUnique();
        b.Entity<Contact>().HasIndex(x => new { x.TenantId, x.PhoneHash }).IsUnique();
        b.Entity<Contact>().HasIndex(x => new { x.TenantId, x.HospitalCustomerId }).IsUnique();
        b.Entity<Opportunity>().HasIndex(x => new { x.TenantId, x.HospitalPurchaseId }).IsUnique();
        b.Entity<Channel>().HasIndex(x => x.PhoneNumberId).IsUnique();
        b.Entity<Conversation>().HasIndex(x => new { x.TenantId, x.ChannelId, x.ContactId }).IsUnique();
        b.Entity<Conversation>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<Message>().HasIndex(x => new { x.TenantId, x.ExternalId }).IsUnique();
        b.Entity<Message>().HasIndex(x => new { x.TenantId, x.RequestKey }).IsUnique();
        // Only the few sends in doubt: the worker sweeps stuck ones every tick and /health/attention counts them across hospitals.
        b.Entity<Message>().HasIndex(x => x.ReceivedAt).HasFilter("\"Status\" IN ('sending', 'uncertain')").HasDatabaseName("IX_Messages_InDoubt");
        b.Entity<Receipt>().HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        b.Entity<Receipt>().HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.Entity<Job>().HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
        b.Entity<Job>().HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
        b.Entity<CalendarLink>().HasIndex(x => new { x.TenantId, x.AppointmentId }).IsUnique();
        b.Entity<Opportunity>().Property(x => x.Value).HasPrecision(14, 2);
        b.Entity<Conversation>().HasOne<Contact>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContactId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Conversation>().HasOne<Channel>().WithMany().HasForeignKey(x => new { x.TenantId, x.ChannelId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Opportunity>().HasOne<Contact>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContactId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Message>().HasOne<Conversation>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConversationId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        var p = protection.CreateProtector("recepcion.at-rest.v1");
        var encrypted = new ValueConverter<string, string>(v => p.Protect(v), v => p.Unprotect(v));
        b.Entity<Contact>().Property(x => x.Phone).HasConversion(encrypted); b.Entity<Contact>().Property(x => x.Name).HasConversion(encrypted);
        b.Entity<Contact>().Property(x => x.Email).HasConversion(encrypted); b.Entity<Message>().Property(x => x.Body).HasConversion(encrypted);
        b.Entity<Opportunity>().Property(x => x.HospitalPurchaseRequest).HasConversion(encrypted!);
        b.Entity<Activity>().Property(x => x.Body).HasConversion(encrypted); b.Entity<Conversation>().Property(x => x.Summary).HasConversion(encrypted);
        b.Entity<Conversation>().Property(x=>x.LastMessage).HasConversion(encrypted!);
        b.Entity<SavedReply>().Property(x=>x.Body).HasConversion(encrypted);
        b.Entity<ContactMemory>().Property(x => x.Value).HasConversion(encrypted);
        b.Entity<PendingAction>().Property(x => x.Body).HasConversion(encrypted);
        b.Entity<ConversationMacro>().Property(x=>x.Note).HasConversion(encrypted);
        b.Entity<Tenant>().Property(x => x.HospitalConnection).HasConversion(encrypted!);
        b.Entity<Tenant>().Property(x => x.Guide).HasConversion(encrypted); b.Entity<Tenant>().Property(x => x.GoogleRefreshToken).HasConversion(encrypted!);
    }
    void Map<T>(ModelBuilder b) where T : TenantRow
    {
        b.Entity<T>().HasKey(x => x.Id); b.Entity<T>().HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Entity<T>().HasQueryFilter(x => x.TenantId == scope.Id);
        b.Entity<T>().HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var e in ChangeTracker.Entries<TenantRow>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (e.Entity.TenantId != scope.Id || scope.Id == Guid.Empty) throw new InvalidOperationException("Tenant boundary violation");
            if (e.Entity is Audit && e.State != EntityState.Added) throw new InvalidOperationException("Audit is append-only");
        }
        return base.SaveChangesAsync(ct);
    }
}
public static class Rules
{
    public static string Phone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone) || phone.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or ' ' or '-' or '(' or ')' or '.'))) throw new ArgumentException("Teléfono internacional inválido");
        var value = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (value.Length is < 8 or > 15 || value.StartsWith("0")) throw new ArgumentException("Teléfono internacional inválido"); return value;
    }
    /// <summary>A contact typed by the team: a Salvadoran number written locally (7000-0000) gets 503, the code WhatsApp delivers it with,
    /// so it is the same contact as its messages. An explicit «+», another length or another first digit is taken as written.</summary>
    public static string ContactPhone(string phone)
    {
        var value = Phone(phone);
        return value.Length == 8 && value[0] is '2' or '6' or '7' && !phone.TrimStart().StartsWith('+') ? "503" + value : value;
    }
    public static string PhoneHash(string phone, string key) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Phone(phone))));
    public static string Required(string? value, int max = 200) => string.IsNullOrWhiteSpace(value) || value.Length > max ? throw new ArgumentException($"Campo requerido (máximo {max} caracteres)") : value.Trim();
    public static bool VerifySignature(byte[] body, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body), Convert.FromHexString(signature)); } catch (FormatException) { return false; }
    }
    public static bool WithinWindow(DateTimeOffset? inbound, DateTimeOffset now) => inbound is not null && inbound <= now && now - inbound < TimeSpan.FromHours(24);
}
